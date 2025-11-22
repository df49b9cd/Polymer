# Diagnostics & Observability

## Runtime Diagnostics Host
- `DiagnosticsControlPlaneHost` lights up `/omnirelay/control/*` routes whenever diagnostics are enabled. Feature flags toggle logging/trace sampling controls, probe dashboards, chaos experiments, leadership snapshots, mesh peer metrics, and shard diagnostics.
- Logging/tracing endpoints accept JSON payloads to change minimum log levels or sampling probabilities on the fly.

## Shard Control Plane
- `ShardDiagnosticsEndpointExtensions` register REST + SSE endpoints under `/control/shards`, `/control/shards/diff`, `/control/shards/watch`, and `/control/shards/simulate`, guarded by `mesh.read` or `mesh.operate` scopes in the `x-mesh-scope` header.
- `ShardControlGrpcService` mirrors the same capabilities over gRPC for strongly-typed clients and watchers.
- Responses serialize via `ShardDiagnosticsJsonContext`, ensuring CLI/HTTP/gRPC share identical DTOs.

## Introspection & Health
- HTTP inbound exposes `/omnirelay/introspect` (dispatcher metadata), `/healthz`, `/readyz`, plus Prometheus metrics endpoints when `diagnostics.prometheus` is enabled.
- ResourceLease components publish SafeTaskQueue/lease metrics for dashboards and integrate with chaos probes.

## Telemetry
- Built-in middleware emits structured logs, OpenTelemetry-compatible traces, metrics, and deadline/timeout events across every transport/codec.
- Configuration toggles route telemetry to OTLP, Prometheus, or custom sinks; `docs/reference/diagnostics.md` lists control-plane commands for operators.

## Operator Tools
- `omnirelay mesh shards *` (see CLI page) leverage the diagnostics host for data-plane investigations, while `omnirelay upgrade` commands orchestrate drain/resume flows using node drain endpoints.