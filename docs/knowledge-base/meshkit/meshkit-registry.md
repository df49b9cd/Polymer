# MeshKit Registry Module

## Purpose
MeshKit.Registry exposes versioned read/write APIs for peers, clusters, versions, and configuration. It fills the role of the control-plane data source that dashboards, CLI commands, and automation rely upon.

## Backlog Reference
- [WORK-013](../../project-board/WORK-013.md) – Read APIs for peers/clusters/config
- [WORK-014](../../project-board/WORK-014.md) – Mutation APIs (cordon/drain/promote/config edits)

## Key Touchpoints
- HTTP/3 + gRPC endpoints under `/meshkit/peers`, `/meshkit/clusters`, `/meshkit/versions`, `/meshkit/config`, and corresponding mutation verbs.
- Streams updates to watchers (CLI `--watch`, dashboards, synthetic probes) with resume tokens and downgrade telemetry.
- Enforces RBAC scopes (`mesh.read`, `mesh.observe`, `mesh.operate`, `mesh.admin`) and optimistic concurrency via ETags.
- Emits audit events accessible to observability tooling (WORK-012, WORK-018) and chaos automation (WORK-022).

## Native AOT readiness
- Set `omnirelay:nativeAot:enabled: true` to route the registry through the trimming-safe dispatcher path; strict mode rejects reflection-based bindings by default.
- Allowed pipeline components when AOT is enabled: built-in RPC middleware (`panic`, `tracing`, `logging`, `metrics`, `deadline`, `retry`, `rate-limiting`, `principal`) and gRPC interceptors (client logging; server logging, transport-security, mesh-authorization, exception-adapter). Custom specs are blocked in strict mode.
- Configuration-driven JSON codec registrations/profiles are disabled under AOT—register codecs programmatically with source-generated contexts.
- Extend the native registry in code if additional middleware/interceptors are required before publishing with `PublishAot=true`.
