# MeshKit Shards Module

## Purpose
MeshKit.Shards is the control-plane service responsible for surfacing shard ownership data, simulations, and diff streams over HTTP/3 + gRPC. It provides the data model that MeshKit.Rebalancer, MeshKit.Registry clients, and the OmniRelay CLI consume.

## Backlog Reference
- [WORK-010](../../project-board/WORK-010.md) – APIs & Tooling
- [WORK-013](../../project-board/WORK-013.md) – Registry Read APIs (shard snapshots)
- [WORK-014](../../project-board/WORK-014.md) – Registry Mutation APIs

## Key Touchpoints
- Exposes `/meshkit/shards` endpoints and watch streams.
- Drives CLI verbs such as `omnirelay mesh shards list|diff|simulate`.
- Persists shard state via the shared stores (`OmniRelay.ShardStore.*`) with optimistic concurrency.
- Emits telemetry consumed by dashboards (WORK-012) and operator alerts (WORK-018).

## Native AOT readiness
- Enable trimming-safe bootstrap by setting `omnirelay:nativeAot:enabled: true` (defaults to strict mode) in module configs; this switches the dispatcher to the no-reflection path.
- Only whitelisted middleware/interceptors are activated in AOT (`panic`, `tracing`, `logging`, `metrics`, `deadline`, `retry`, `rate-limiting`, `principal` + gRPC server/client logging, transport-security, mesh-authorization, exception-adapter). Custom inbounds/outbounds are rejected when strict.
- Configuration-based JSON codec registrations/profiles are blocked in AOT; use source-generated codecs or programmatic registration instead.
- If additional components are required, register them via code or extend the native registry before publishing as `PublishAot=true`.
- AOT smoke coverage now lives in `tests/OmniRelay.MeshKit.AotSmoke` (minimal gRPC host wiring Shards + Leadership against in-memory stores). Run `./eng/run-aot-publish.sh linux-x64 Release` to publish the smoke host and CLI as native AOT artifacts; the script fails if trimming/AOT analyzers surface new warnings.
