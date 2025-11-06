# HTTP/3 Rollback Runbook

Goal: Safely revert listeners and clients to HTTP/1.1/2 if HTTP/3 issues arise.

> See also: HTTP/3/QUIC FAQ at `docs/reference/http3-faq.md` for common triage steps.

## Preconditions

- Access to configuration and deployment pipelines
- Observability dashboards available (protocol metrics, fallback rates, error codes)

## Rollback switches

Server (inbounds):

- HTTP inbound: set `http.inbounds.*.runtime.enableHttp3: false`
- gRPC inbound: set `grpc.inbounds.*.runtime.enableHttp3: false`

Client (outbounds):

- HTTP outbound: set `outbounds.http.*.runtime.enableHttp3: false`
- gRPC outbound: set `outbounds.grpc.*.runtime.enableHttp3: false`

CLI (ad hoc):

- Remove `--http3` and `--grpc-http3` flags from CLI commands when testing

## Procedure

1) Stage and canary

- Apply config toggles to staging. Validate via:
  - `omnirelay introspect --url https://<host>/omnirelay/introspect --format text` (confirm listeners)
  - `omnirelay bench http --url https://<host> --procedure ping --rps 50 --duration 30s` (no `--http3`)
  - `omnirelay bench grpc --address https://<host> --service test --procedure ping --rps 50 --duration 30s` (no `--grpc-http3`)
- Check dashboards: fallback rate should approach 100% (all traffic on HTTP/2/1.1), QUIC handshakes should drop to zero

1) Production canary

- Roll toggles for a small canary slice (e.g., 1–5%)
- Verify:
  - Error rate unchanged or improved
  - Latency within acceptable ranges
  - Fallback rate ~100%

1) Full rollout

- Roll toggles to 100%
- Continue monitoring for at least 30 minutes

## Verification checklist

- No QUIC handshakes observed (server metrics, logs)
- `Rpc-Protocol` headers and telemetry show HTTP/2/1.1 only
- No increase in 5xx or gRPC Unavailable/DeadlineExceeded errors
- Client-side logs free of HTTP/3 negotiation errors

## Post-rollback

- Open an incident retro with findings and root cause
- Update configuration defaults or documentation as needed
- Optionally disable HTTP/3 advertisement at the edge (remove `alt-svc`) if applicable

## Appendix: Quick commands

- HTTP quick probe without HTTP/3:

```bash
omnirelay bench http --url https://127.0.0.1:8443 \
  --procedure ping --rps 50 --duration 30s
```

- gRPC quick probe without HTTP/3:

```bash
omnirelay bench grpc --address https://127.0.0.1:8443 \
  --service test --procedure ping --rps 50 --duration 30s
```

- curl probe (if built with HTTP/3):

```bash
curl --http3 https://127.0.0.1:8443/healthz || echo "HTTP/3 probe failed as expected after rollback"
```
