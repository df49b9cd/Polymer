# OmniRelay Transport Policy Engine

WORK-001 introduces a governance layer for mesh-internal transports so diagnostics/control-plane endpoints remain HTTP/3-first with protobuf encodings. This document summarizes the configuration surface, CLI workflows, and telemetry you can use to enforce the policy.

## Configuration

Add a `transportPolicy` section under the `omnirelay` root. The engine ships with immutable defaults:

- `control-plane` category allows only `grpc` transport with `protobuf` encoding.
- `diagnostics` category allows `http3` transport with `json` or `protobuf` encoding.

You can refine or override the defaults per cluster:

```json
{
  "omnirelay": {
    "service": "mesh.control",
    "transportPolicy": {
      "categories": {
        "diagnostics": {
          "allowedTransports": ["http3", "http2"],
          "allowedEncodings": ["json", "protobuf"],
          "preferredTransport": "http3",
          "requirePreferredTransport": true
        }
      },
      "exceptions": [
        {
          "name": "legacy-json-http2",
          "category": "diagnostics",
          "appliesTo": ["diagnostics:http"],
          "transports": ["http2"],
          "encodings": ["json"],
          "reason": "Regional observability stack lacks QUIC",
          "expiresAfter": "2025-12-31T00:00:00Z",
          "approvedBy": "transport-governance"
        }
      ]
    }
  }
}
```

Endpoints are referenced by stable names:

| Endpoint | Category | Description |
| --- | --- | --- |
| `diagnostics:http` | `diagnostics` | HTTP listener exposing `/omnirelay/control/*`, docs, probes. |
| `control-plane:grpc` | `control-plane` | MeshKit leadership/shard control gRPC endpoint. |

## CLI Validation

Use `omnirelay mesh config validate` to evaluate layered configs (including `--set` overrides) before publishing:

```bash
omnirelay mesh config validate \
  --config config/appsettings.json \
  --set omnirelay:diagnostics:controlPlane:httpRuntime:enableHttp3=true
```

- Exit code `0` means the transport policy is satisfied.
- Exit code `1` indicates at least one violation; the CLI prints actionable hints (e.g., enable HTTP/3 or register an exception).
- Pass `--format json` for CI/automation pipelines:
  ```bash
  omnirelay mesh config validate --config appsettings.json --format json | jq .
  ```

`omnirelay config validate` still performs full dispatcher binding; use the mesh command when you need fast policy feedback without TLS certificates or inbounds present.

### Text Output

`omnirelay mesh config validate` now emits remediation hints and negotiated protocol state for every endpoint plus a roll-up so operators can react quickly:

```
Transport policy evaluation for section 'omnirelay':
  [VIOLATION] diagnostics:http (diagnostics) -> http2/json
      transport 'http2' downgrades preferred 'http3'
      Negotiated protocol: http2-fallback
      Hint: Set diagnostics.controlPlane.httpRuntime.enableHttp3 = true to satisfy HTTP/3-first policy.
Summary: total=2, compliant=1, exceptions=0, violations=1
Downgrade ratio: 50.0 %
Policy violations detected. See details above.
```

### JSON Output

Automation can use the JSON payload to gate CI. Alongside the original findings array you now receive a `summary` block and richer per-endpoint metadata:

```json
{
  "section": "omnirelay",
  "hasViolations": true,
  "hasExceptions": false,
  "summary": {
    "total": 2,
    "compliant": 1,
    "excepted": 0,
    "violations": 1,
    "violationRatio": 0.5
  },
  "findings": [
    {
      "endpoint": "diagnostics:http",
      "category": "diagnostics",
      "transport": "http2",
      "encoding": "json",
      "http3Enabled": false,
      "status": "violatespolicy",
      "message": "transport 'http2' downgrades preferred 'http3'",
      "hint": "Set diagnostics.controlPlane.httpRuntime.enableHttp3 = true to satisfy HTTP/3-first policy."
    }
  ]
}
```

`violationRatio` is a normalized downgrade metric (0.0–1.0) that feeds dashboards and CI alerts.

## Telemetry

The engine records metrics via the `OmniRelay.Transport.Policy` meter:

| Metric | Description | Key Tags |
| --- | --- | --- |
| `omnirelay.transport.policy.endpoints` | Number of evaluated endpoints | `omnirelay.transport.endpoint`, `omnirelay.transport.category`, `omnirelay.transport.status` |
| `omnirelay.transport.policy.violations` | Violations detected at startup | Same as above |
| `omnirelay.transport.policy.exceptions` | Approved exceptions used at runtime | Same as above |
| `omnirelay.http.client.fallbacks` | HTTP/3 downgrade counter (existing metric) | `rpc.service`, `rpc.procedure`, `http.observed_protocol` |

Add these meters to your dashboards to visualize downgrade ratios over time and alert when exceptions linger past their expiration date.

## Samples

`samples/Observability.CliPlayground/appsettings.json` now includes a policy exception that keeps local JSON collectors on HTTP/2 while still documenting the expiry and approver. Use it as a template whenever you need to stage temporary downgrades.
