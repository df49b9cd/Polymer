# WORK-001 – OmniRelay Transport & Encoding Policy Engine

## Goal
Keep OmniRelay focused on transport governance by enforcing HTTP/3-first policies, downgrade telemetry, and encoding rules while providing CLI validation so MeshKit and third-party modules can rely on a predictable transport layer.

## Scope
- Extend OmniRelay configuration schema to declare allowed transports (`http3`, `http2`, `grpc`) and encodings (`protobuf`, `json`, `raw`) per endpoint category (control-plane, diagnostics, optional exceptions).
- Implement validation at startup + `omnirelay mesh config validate` to block disallowed combinations and suggest remediation.
- Emit telemetry counters for negotiated transports/encodings, downgrade ratios, and policy violations with dashboards/alerts.
- Document policy configuration, override workflows, and CLI validation paths.

## Requirements
1. **Immutable defaults** – Mesh-internal endpoints must prefer HTTP/3 + Protobuf; exceptions require explicit policy entries and RBAC approvals.
2. **Downgrade awareness** – Record downgrade reasons in metrics/logs and expose through CLI/diagnostics.
3. **CLI validation** – Provide fast validation before deployment, including inline config fragments and CI automation integration.
4. **Observability** – Export metrics/dashboards showing transport adoption trends and highlight legacy usage.
5. **AOT gate** – OmniRelay transport binaries must publish as native AOT with policy engine enabled (WORK-002).

## Deliverables
- Updated configuration schema + validation library.
- Telemetry instrumentation + dashboards/alerts for transport adoption.
- CLI command updates + documentation.

## Acceptance Criteria
- Invalid transport/encoding combos fail fast with actionable errors (CLI + startup) and do not start the host.
- Metrics/alerts show HTTP/3 adoption and highlight exceptions.
- Samples/docs demonstrate approved exceptions (e.g., HTTP/1 JSON) using the new policy engine.
- Native AOT build/tests succeed per WORK-002..WORK-005.

## Testing Strategy
- Unit tests for policy parsing, validation, telemetry counters, CLI output.
- Integration tests launching OmniRelay hosts with valid/invalid configs, forcing downgrades, verifying telemetry + CLI validation.
- Feature tests enabling/disabling exceptions and ensuring dashboards/CLI reflect state.
- Hyperscale tests rolling policy updates across many services ensuring downgrade monitoring/alerting scales.

## References
- `docs/architecture/transport-layer-vision.md`
- `docs/project-board/transport-layer-plan.md`

## Status & Validation
- Completed on November 19, 2025 with CLI summaries, JSON schema updates, and telemetry hints wired into `omnirelay mesh config validate`.
- Validation commands:
  - `dotnet test tests/OmniRelay.Configuration.UnitTests/OmniRelay.Configuration.UnitTests.csproj --filter TransportPolicy`
  - `dotnet test tests/OmniRelay.FeatureTests/OmniRelay.FeatureTests.csproj --filter TransportPolicy`
  - `dotnet test tests/OmniRelay.IntegrationTests/OmniRelay.IntegrationTests.csproj --filter TransportPolicyIntegrationTests`
  - `dotnet test tests/OmniRelay.HyperscaleFeatureTests/OmniRelay.HyperscaleFeatureTests.csproj --filter TransportPolicyHyperscaleFeatureTests`
