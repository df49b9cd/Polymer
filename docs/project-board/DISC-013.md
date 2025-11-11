# DISC-013 – Transport & Encoding Policy Engine

## Goal
Enforce governed transport/encoding profiles (default gRPC/HTTP3 + Protobuf with HTTP/2 downgrade) while allowing opt-in edge configurations with validation and telemetry.

## Scope
- Extend dispatcher/host configuration schema to declare per-endpoint transport (`http1`, `http2`, `http3`, `grpc`) and encoding (`protobuf`, `json`, `raw`).
- Implement validation rules: mesh-internal endpoints must include gRPC/HTTP3+Protobuf; raw encodings require ACLs; HTTP/1.1 disabled for control-plane.
- Emit telemetry counters/labels for negotiated transport/encoding; add alerts for unsupported combinations.
- Update documentation, samples, and docker assets to showcase configuration knobs.

## Requirements
1. **Downgrade awareness** – Record when traffic downgrades to HTTP/2 and expose in metrics/logs.
2. **Policy config** – Provide central policy document/CRD defining allowed combos per role/namespace; enforce at startup and hot reload when possible.
3. **Error UX** – Validation failures must produce actionable error messages referencing offending endpoint/config key.
4. **CLI support** – `omnirelay mesh config validate` should surface policy violations before deployment.
5. **Observability** – Add dashboards for transport/encoding adoption trends.

## Deliverables
- Config schema + validation library.
- Telemetry instrumentation + Grafana panels.
- CLI validation command updates.
- Documentation updates (service-discovery, samples README).

## Acceptance Criteria
- Attempting to start a host with disallowed transport/encoding fails fast with clear error.
- Metrics/alerts show adoption of HTTP/3 defaults and highlight legacy usage.
- Samples demonstrate enabling an HTTP/1.1 JSON endpoint alongside the canonical gRPC profile.

## References
- `docs/architecture/service-discovery.md` – “Transport & encoding strategy”, “Transport/encoding governance”.
