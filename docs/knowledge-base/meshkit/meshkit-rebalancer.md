# MeshKit Rebalancer Module

## Purpose
MeshKit.Rebalancer ingests MeshKit.Shards data, peer health signals, and Hugo backpressure metrics to generate safe shard movement plans. It supports dry-run previews, approval workflows, execution tracking, and emits controller telemetry.

## Backlog Reference
- [WORK-011](../../project-board/WORK-011.md) – Controller + policy engine
- [WORK-012](../../project-board/WORK-012.md) – Observability package (dashboards/alerts)

## Key Touchpoints
- Consumes MeshKit.Shards snapshots and peer health feeds.
- Hosts `/meshkit/rebalance-plans` REST/gRPC surfaces for plan listing, approvals, and execution monitoring.
- Powers CLI commands `omnirelay mesh shards rebalance *` and operator runbooks referenced in WORK-011/012.
- Supplies metrics (`meshkit_rebalance_*`) to dashboards/alerts defined in WORK-012 and to synthetic probes/chaos testing (WORK-020/021).

## Native AOT readiness
- Set `omnirelay:nativeAot:enabled: true` to force the MeshKit dispatcher onto the trimming-safe path; strict mode (default) blocks reflection-based bindings.
- Allowed components under AOT: built-in RPC middleware (`panic`, `tracing`, `logging`, `metrics`, `deadline`, `retry`, `rate-limiting`, `principal`) and gRPC interceptors (client logging; server logging, transport-security, mesh-authorization, exception-adapter). Custom specs are rejected when strict.
- Configuration-driven JSON codec registrations/profiles are disabled in AOT; use source-generated contexts/codecs registered in code.
- Extend the native registry in code if the rebalancer needs extra middleware/interceptors; do so before publishing with `PublishAot=true`.
