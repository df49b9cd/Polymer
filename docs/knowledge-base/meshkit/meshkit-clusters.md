# MeshKit Cluster Descriptor Module

## Purpose
MeshKit.ClusterDescriptors defines geo/region metadata, states, priorities, and governance required for multi-cluster routing and failover. It feeds MeshKit.CrossClusterFailover and operator tooling with authoritative topology data.

## Backlog Reference
- [WORK-015](../../project-board/WORK-015.md) – Descriptor schema + APIs

## Key Touchpoints
- Persists descriptor schema (`clusterId`, region, state, priority, failover policy, annotations, owners) with audit history.
- Extends MeshKit.Registry surfaces so CLI/dashboards can list/filter descriptors.
- Provides readiness signals consumed by failover automation (WORK-016) and operator runbooks (WORK-017, WORK-018).

## Native AOT readiness
- Enable trimming-safe startup with `omnirelay:nativeAot:enabled: true`; strict mode (default) blocks reflection-based bindings.
- AOT permits only built-in middleware/interceptors: panic, tracing, logging, metrics, deadline, retry, rate-limiting, principal plus gRPC client logging and server logging/transport-security/mesh-authorization/exception-adapter. Custom specs are rejected in strict mode.
- JSON codec registrations/profiles from configuration are disabled under AOT—register codecs programmatically using source-generated contexts when needed.
- Extend the native registry in code before publishing with `PublishAot=true` if additional components are required for descriptor APIs.
