# OmniRelay Transport Stack Vision

## Purpose
- Anchor OmniRelay as the opinionated transport fabric embedded by services that need deterministic outbound or inbound HTTP/gRPC workloads.
- Delegate mesh/service-discovery responsibilities to MeshKit and concurrency/runtime primitives to Hugo so each layer has a single clear concern.
- Provide a blueprint for building delta-lake-style multi-service systems on Hugo + OmniRelay + MeshKit without re-deriving control-plane designs per team.

## Layered Responsibilities
| Layer | Primary Responsibilities | Key Assets |
| --- | --- | --- |
| Hugo | Deterministic task orchestration, SafeTaskQueue, backpressure primitives, resumption and reliability semantics. | SafeTaskQueue, TaskQueueBackpressureMonitor, ResourceLease dispatcher core. |
| OmniRelay | HTTP/3-first transport runtime (HTTP, gRPC, SSE), middleware (retry, rate limit, tracing), dispatcher APIs, CLI plumbing, serialization contexts, shard runtime surfaces. | `src/OmniRelay/Core/*`, `src/OmniRelay/Transport/*`, CLI `omnirelay mesh *`. |
| MeshKit | Gossip, membership, leadership, shard registry, rebalancer, replication/failover, diagnostics/control endpoints leveraging OmniRelay transport. | `MeshKit.*` projects built from the new WORK-010..WORK-016 roadmap. |

## Interaction Model
1. **Runtime boot**: Hugo hosts spin up OmniRelay dispatcher instances using the bootstrap harness (WORK-009). OmniRelay loads transport/TLS options via the shared TLS manager and registers control-plane hosts through transport builders/factories (now embedded in WORK-006/WORK-008).
2. **Control-plane overlay**: MeshKit composes OmniRelay transports using the shared client helpers (WORK-006), publishes gossip + leadership feeds (legacy implementations moving into MeshKit), and manages shard/control APIs (WORK-010..WORK-014) over HTTP/3+gRPC.
3. **Operator workflows**: The `omnirelay mesh` CLI speaks exclusively to OmniRelay/MeshKit HTTP/3 + gRPC endpoints scoped by MeshKit-provided auth/registry metadata; Hugo instrumentation flows upward via shared telemetry modules and the MeshKit observability tracks (WORK-012, WORK-018).
4. **Observability**: MeshKit emits peer/control-plane telemetry (WORK-012, WORK-018, WORK-020) while OmniRelay records transport metrics; dashboards join both views to show transport health vs. control-plane directives.
## Separation of Concerns
- **Data plane (OmniRelay)**: Maintains HTTP/3-first listeners, middleware, codecs, dispatcher extensibility, CLI UX, deterministic serialization contexts, and test harnesses. No ownership of gossip/leadership, peer registries, or rebalancer logic.
- **Control plane (MeshKit)**: Owns gossip, leadership, shard registry/rebalancer, replication/failover, bootstrap/join, operator APIs, dashboards, chaos tooling, and RBAC. Implements the WORK-010..WORK-022 backlog while consuming OmniRelay transports.
- **Execution plane (Hugo)**: Supplies SafeTaskQueue, deterministic state stores, rate-limit/backpressure signals, and concurrency primitives consumed by OmniRelay + MeshKit. Provides the "railway" semantics for building multi-service experiences.

## Architectural Requirements
1. **Transport purity**: Any new dispatcher feature must articulate which layer owns persistence or coordination; OmniRelay tickets should refuse features that belong in MeshKit or Hugo.
2. **Contract-first control plane**: MeshKit exports control APIs (WORK-010..WORK-016) as the single source for CLI, dashboards, and automation. OmniRelay’s CLI commands rely on MeshKit-provided scope headers and auth cues.
3. **Native AOT everywhere**: All three layers must satisfy WORK-002..WORK-005 so transport/control binaries run as AOT for CI, release, and hyperscale test gates.
4. **Knowledge base alignment**: `docs/knowledge-base` entries reference this document so contributors know where to place new ADRs or backlog items.
5. **Shared testing strategy**: Unit, integration, feature, hyperscale suites continue to validate OmniRelay transport behavior while MeshKit inherits control-plane harnesses via the shared testing toolkit.

## Migration Considerations
- Carve MeshKit source from `src/OmniRelay.ControlPlane/Core/Gossip`, `Core/Leadership`, `Core/Shards`, diagnostics control hosts, bootstrap tooling, and CLI flows that manipulate registry/control-plane state; keep `OmniRelay.DataPlane` free of gossip/leadership/shard hosting code.
- Maintain OmniRelay transport API compatibility by hosting MeshKit control-plane surfaces via `HttpControlPlaneHostBuilder` and `GrpcControlPlaneHostBuilder` until MeshKit provides its own packaging.
- Update documentation + samples (WORK-017) so every workflow references the layered model (Hugo → OmniRelay → MeshKit) and relocates governance content (shards, rebalancer) under MeshKit sections.
## Key Interfaces to Maintain
- **Transport builders/factories**: Remain the boundary between OmniRelay and MeshKit, guaranteeing HTTP/3-first semantics, downgrade telemetry, and TLS/middleware parity.
- **Diagnostics runtime & peer health kit**: Shared package ensures `/omnirelay/control/*` endpoints persist even as MeshKit relocates control-plane ownership.
- **CLI + automation surface**: CLI commands stay in OmniRelay but call MeshKit endpoints; command contracts cannot drift from MeshKit OpenAPI/Protobuf specs.

## References
- WORK-001..WORK-022 for the active roadmap across OmniRelay, MeshKit, and operator experience tracks.
- `docs/reference/omnirelay-transport-story.md` and `docs/reference/meshkit-control-plane-story.md` for the originating narratives.
- Legacy backlog notes (archived outside this board) for historical context.
- `docs/reference/transport-policy.md` for HTTP/3 + Protobuf policy configuration, exceptions, and CLI validation workflows.
