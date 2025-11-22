# MeshKit Control-Plane Story

## Goal
- Extract and standardize the control-plane capabilities (gossip, leadership, SafeTaskQueue orchestration, diagnostics, Shards) currently embedded in OmniRelay so any service can compose a mesh-aware peer layer alongside Hugo and OmniRelay.

## Scope
- New MeshKit library providing peering foundations: gossip membership, leadership coordination, SafeTaskQueue backpressure/replication wiring, diagnostics/control endpoints.
- Interfaces for hosts (OmniRelay or other services) to plug transports, policies, and automation.
- Excludes transport runtime (OmniRelay) or low-level concurrency primitives (Hugo).

## Requirements
1. Gossip subsystem:
   - HTTP/3 listener + client fanout similar to `src/OmniRelay.ControlPlane/Core/Gossip/MeshGossipHost.cs`.
   - Membership table (`MeshGossipMembershipTable`) with metadata versioning, status transitions, RTT tracking.
   - TLS policy options, seed peer bootstrapping, diagnostics (metrics/logs).
2. Leadership subsystem:
   - Coordination APIs mirroring `src/OmniRelay/Core/Leadership/*` (tokens, coordinator, GRPC control service).
   - Event dissemination (SSE/gRPC stream) for automation.
3. SafeTaskQueue orchestration hooks:
   - Integrate Hugo `TaskQueueBackpressureMonitor` and `TaskQueueReplicationSource`.
   - Propagate peer health via `PeerLeaseHealthTracker` equivalent.
4. Diagnostics package:
   - Metrics (member counts, gossip RTT, leadership state, backpressure, replication lag).
   - Control-plane endpoints/CLI integration (e.g., `/omnirelay/control/backpressure`, `/control/events/leadership`).
5. Configuration & hosting:
   - `MeshKitOptions` for fanout, biopsy intervals, TLS, instrumentation.
   - ASP.NET Core hosting extensions (AddMeshKit, MapMeshKitControlEndpoints).
6. Documentation + samples demonstrating OmniRelay + MeshKit wiring and independent adoption.

## Deliverables
- MeshKit source tree (new project or folder) with gossip, leadership, SafeTaskQueue orchestration, diagnostics modules.
- Docs: architecture overview, hosting guide, API reference, migration plan from OmniRelay-internal code.
- Samples (e.g., extend `samples/ResourceLease.MeshDemo`) using MeshKit instead of bespoke dispatcher code.
- CI/test coverage + NuGet packaging.

## Acceptance Criteria
1. MeshKit hosts can form a gossip mesh, elect leaders, and emit diagnostics without depending on OmniRelay internals.
2. OmniRelay integrates MeshKit for control-plane duties, removing duplicated code (MeshGossip*, Leadership*, ResourceLease backpressure/replication plumbing).
3. MeshKit emits metrics/traces matching existing OmniRelay instrumentation (for seamless dashboard migration).
4. Control endpoints expose the same contracts currently documented (e.g., `/omnirelay/control/backpressure`), enabling CLI compatibility.
5. Documentation clearly delineates responsibilities between Hugo, MeshKit, and OmniRelay.

## References
- `src/OmniRelay.ControlPlane/Core/Gossip/*`
- `src/OmniRelay.ControlPlane/Core/Leadership/*`
- `src/OmniRelay/Dispatcher/ResourceLeaseBackpressure*.cs`
- `docs/reference/distributed-task-leasing.md`
- `docs/architecture/service-discovery.md`
- Samples (`samples/ResourceLease.MeshDemo`, `samples/Quickstart.Server`)

## Testing Strategy
- Unit tests for membership tables, TLS policies, leadership coordination, backpressure listeners.
- Integration tests spinning up multiple MeshKit nodes exchanging gossip/leadership events.
- Feature tests embedding MeshKit with OmniRelay, verifying control-plane + transport interplay.

### Unit Test Scenarios
- Membership table state transitions (Healthy → Suspect → Left) with metadata version precedence.
- Gossip envelope serialization/deserialization and TLS validation policies.
- Leadership token acquisition/renewal, ensuring single active leader per shard.
- Backpressure listener channel behaviour (latest snapshot, bounded history).

### Integration Test Scenarios
- Multi-node gossip cluster exchanging health/metadata, verifying fanout, retries, and metrics.
- Leadership election across nodes with failover simulation (leader crash → new leader).
- SafeTaskQueue orchestrator wiring: applying MeshKit monitors to queue workloads and verifying signals across nodes.

### Feature Test Scenarios
- Combined OmniRelay + MeshKit deployment in containers demonstrating peer discovery, leadership, backpressure control endpoints, and CLI watch commands.
- Disaster-recovery drills (network partitions, TLS rotation) validating MeshKit resiliency.
- Auto-scaling automation consuming MeshKit diagnostics to trigger scale actions.
