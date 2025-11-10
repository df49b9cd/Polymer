## OmniRelay RPC Mesh

This document outlines how to operate OmniRelay as a self-healing, peer-aware RPC Mesh. It captures the current building blocks and a concrete backlog to finish the mesh experience.

### Release Status
- OmniRelay is currently in **alpha (v0.4.1)**. APIs may change, and we are free to introduce breaking adjustments while we converge on the final mesh shape. Document every major change and provide upgrade notes, but do not block necessary redesigns on backward compatibility at this stage.

### Goals
- Every metadata node exposes the same lease-aware RPC surface through OmniRelay.
- Peers route work based on live health/lease state rather than static config.
- Replication ensures all nodes replay the same ordered mutations so recovery is deterministic and split-brain free.
- Backpressure and telemetry keep the mesh resilient under load spikes.

### Core Building Blocks
1. **Resource Lease Dispatcher Component**
   - The `ResourceLeaseDispatcherComponent` models a generic work-queue item (`ResourceLeaseWorkItem` + `ResourceLeaseItemPayload`). The payload carries the explicit `ResourceType`/`ResourceId` pair (plus partition key, attributes, opaque body, and request id) so workflows, ingestion, ML jobs, or any arbitrary domain can participate without translating into table semantics.
   - The dispatcher already exposes the neutral `resourcelease::*` surface (`enqueue`, `lease`, `complete`, `heartbeat`, `fail`, `drain`, `restore`) via `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs`.
   - Keep using the same SafeTaskQueue + replication/backpressure plumbing; only the envelope and semantics are resource-aware now.
   - Backed by a local `SafeTaskQueue<ResourceLeaseWorkItem>` with configurable capacity, lease duration, and backpressure options.
2. **Principal + Peer Health Middleware**
   - Apply `PrincipalBindingMiddleware` before lease procedures so every request carries a normalized identity (`rpc.principal`) and optional thumbprint.
   - Share a `PeerLeaseHealthTracker` across dispatchers and peer choosers (`RoundRobinPeerChooser`, `FewestPendingPeerChooser`, etc.) so lease assignments/heartbeats update cluster health.
3. **Replication + Deterministic Capture**
   - Plug an `IResourceLeaseReplicator` (e.g., `InMemoryResourceLeaseReplicator` or a custom sink) into each component to sequence every queue mutation.
   - Use the new durable replicators when you need persistence beyond process memory: `SqliteResourceLeaseReplicator` (writes inside a SQLite database), `ObjectStorageResourceLeaseReplicator` (emits JSON blobs to S3/GCS/Azure-compatible stores via `IResourceLeaseObjectStore`), or `GrpcResourceLeaseReplicator` (ships events to a remote `ResourceLeaseReplicatorGrpc` service while still applying local sinks).
   - Optionally add `DeterministicResourceLeaseCoordinator` (via `ResourceLeaseDeterministicOptions`) so the same ordered events are persisted in an effect store for replay.
   - Harden deterministic capture by swapping in the production adapters: `SqliteDeterministicStateStore` for lightweight SQL durability and `FileSystemDeterministicStateStore` for human-inspectable JSON blobs on disk. Both satisfy `IDeterministicStateStore` and drop in without touching dispatcher code.
4. **Backpressure + Metrics**
   - Configure `TaskQueueOptions.Backpressure` thresholds and wire an `IResourceLeaseBackpressureListener` to fan out pause/resume signals to upstream throttles.
   - Emit `ResourceLeaseMetrics` (`pending`, `active`, `backpressure.transitions`) to whichever telemetry sink each node exposes.
5. **Transport/Codec Parity**
   - Use dispatcher JSON helpers (or codegen) so the mesh exposes identical codecs and middleware stacks over HTTP, gRPC, etc.
   - Register outbound bindings for peer-to-peer calls (if needed) via `DispatcherOptions.AddUnaryOutbound` et al.

### Mesh Operation Flow
1. Client enqueues work via `resourcelease::enqueue`. Backpressure guards short-circuit if pending depth breaches the high watermark.
2. Peers call `resourcelease::lease`. SafeTaskQueue hands out the next pending item, returning a `ResourceLeaseOwnershipHandle` and recording the owning peer ID.
3. Lease owner runs work. Heartbeats keep ownership alive; failure or lease expiry requeues the item.
4. Each mutation publishes a `ResourceLeaseReplicationEvent` to all sinks and (optionally) to the deterministic effect store. Checkpointing sinks dedupe replays.
5. Peer choosers consult `PeerLeaseHealthTracker` before routing new work. Gossip (attributes/metadata) piggybacks on lease events so downstream systems know which peers are healthy.
6. On recovery, a node replays replication events (respecting checkpoints) and resumes leasing once caught up. Deterministic capture guarantees idempotent side effects during replay.

### Best Practices
- **Normalize caller identity early.** Place `PrincipalBindingMiddleware` (or equivalent) at the front of every inbound stack so lease owner IDs and replication logs always contain a canonical principal. Makes audit, health filtering, and debugging much easier.
- **Share health tracker instances.** Reuse a single `PeerLeaseHealthTracker` per node and inject it into both the ResourceLease dispatcher and any peer chooser you build. That keeps routing decisions and gossip in sync.
- **Treat replication as authoritative.** Run at least one `IResourceLeaseReplicationSink` (even if in-memory) everywhere, checkpoint aggressively, and persist the stream if you need recovery. Consumer apps should replay from the log instead of inferring state from ad-hoc APIs.
- **Watch backpressure + queue depth.** Set conservative high/low watermarks; instrument `ResourceLeaseMetrics` and react to `IResourceLeaseBackpressureListener` callbacks by shedding load upstream. Queues should stay shallow; long tails hint at stuck peers.
- **Scope queues/shards by fault domain.** If a single SafeTaskQueue becomes hot, split workloads by `ResourceType`, `ResourceId` prefixes, or other lease identity fields. Keep replication fan-out per shard small to reduce blast radius.
- **Version deterministic captures.** When `ResourceLeaseDeterministicOptions` is in play, pick stable `ChangeId`/version ranges, record effect IDs in observability, and rotate versions during upgrades to avoid replay surprises.
- **Test failure drills.** Regularly simulate peer loss, duplicate lease attempts, and partition recovery (using integration tests or chaos scripts) to ensure fencing + replication behave as expected before production incidents.

### Lakehouse Data Fabric Deployment
Running a data-fabric stack (catalogs, ingestion, orchestration, read models, etc.) as an OmniRelay RPC mesh follows a consistent recipe:

1. **Ship OmniRelay with every service.** Each service (metadata catalog, event bus, ingestion, domain APIs, read models, orchestration, governance, security brokers, compute gateways) hosts its own dispatcher instance and registers the shared lease surface (`resourcelease::*`, replacing the previous `tablelease::*` namespace).
2. **Normalize identity + security.** Apply `PrincipalBindingMiddleware` everywhere with the organization’s mTLS/JWT headers so `rpc.principal` is stable. Reuse existing CA/STS components for certs/tokens; OmniRelay just consumes the headers.
3. **Share health + routing.** Instantiate one `PeerLeaseHealthTracker` per service process, pass it to the lease dispatcher and any peer chooser (round-robin, fewest-pending). Routing decisions, gossip, and diagnostics now share the same live view of peer state.
4. **Enable replication/deterministic capture.** Point every dispatcher at an `IResourceLeaseReplicator` hub (start with `InMemoryResourceLeaseReplicator`, evolve to gRPC/SQL sinks). Mission-critical services should add `DeterministicResourceLeaseCoordinator` backed by a durable `IDeterministicStateStore` so recovery replays effects exactly once.
5. **Use SafeTaskQueue for domain work.** Encode resource identity in the lease payload via the explicit `ResourceType`/`ResourceId` pair (plus `PartitionKey`/`Attributes`) for: table manifest updates, CDC shard ownership, workflow steps, ingestion checkpoints, materialized-view rebuilds, orchestration sagas, etc. Multiple peers can lease different items simultaneously for parallelism; fences prevent duplicate processing.
6. **Segment by shard/fault domain.** Large fabrics typically run multiple dispatcher instances or queue partitions per domain (per table family, CDC feed, workflow). Route items via payload attributes to keep replication/backpressure scoped.
7. **Wire observability + governance.** Emit `ResourceLeaseMetrics` and attach replication sinks feeding lineage/monitoring services so governance dashboards know which peer touched which resource, when, and with which principal.
8. **Integrate per service:**
   - **Lakehouse table format & schema registry:** Serialize schema/table mutations through leases; replication feed drives lineage/change data subscriptions.
   - **Event bus / ingestion producers:** Use leases to coordinate partition ownership; health tracker fails over to another producer when heartbeats stop.
   - **Domain APIs / read models:** Run background projections and rebuilds via leases to avoid double-processing.
   - **Query/GraphQL gateways & compute engines:** Lease long-running compute jobs; heartbeats signal liveness, failure requeues work.
   - **Observability/governance:** Subscribe to replication events to enrich lineage graphs and monitor anomalous peers.
   - **Security & identity:** Provide certs/tokens; OmniRelay propagates principals into metadata for audit.
   - **Orchestration/workflow services:** Model saga steps as lease payloads so retries/compensations follow the same fencing semantics.
   - **Change data feed subscribers:** Represent feed checkpoints as lease items so multiple consumers don’t double-apply deltas.
9. **Operational checklist:** package OmniRelay, configure dispatcher (service name, transports, middleware), register lease component, attach replication/deterministic stores, monitor metrics/backpressure, and run failure drills (kill peers, break network) before production rollout.

### Enhancements Needed for Data-Fabric-Grade Mesh
OmniRelay already supplies the core pieces (SafeTaskQueue, replication, health gossip), but turning it into a first-class RPC mesh for a lakehouse fabric requires a few upgrades:
- **Resource-agnostic lease contracts.** Extract `ResourceLease*` DTOs/handlers into resource-neutral aliases so catalog, event bus, or workflow services can all describe their resources without forcing “table” semantics. Keep compatibility adapters.
  - Since we are still in alpha, plan for a wholesale rename instead of long-term shims.
- **Durable replication hub + sinks.** Ship an `IResourceLeaseReplicator` implementation that persists sequences (SQL, object storage, gRPC stream) and sample sinks (e.g., governance feed, lineage store). Today’s in-memory replicator is best-effort only.
- **Pluggable deterministic state stores.** Provide production-ready `IDeterministicStateStore` adapters (SQL, Cosmos DB, Redis) and configuration guides so deterministic replay is practical outside tests.
- **Operational playbooks + tooling.** Deliver a mesh deployment guide, health endpoints exposing `PeerLeaseHealthTracker` snapshots, and `ResourceLease` CLI tooling for drain/restore/backpressure inspection.
- **Backpressure + routing hooks.** Add reusable `IResourceLeaseBackpressureListener` adapters (hooking RateLimitingMiddleware, traffic shutters) and document patterns for multi-shard routing so fabrics can throttle or reroute automatically.
- **Mesh-wide observability.** Define standard metrics/OTLP spans for replication lag, lease churn, backpressure transitions, and expose dashboards/alerts so governance/ops teams can monitor the mesh.
- **Failure/chaos validation.** Include automated tests or scripts that kill peers, corrupt replicas, and partition networks to prove the mesh self-heals without split-brain before production rollout.

### Evolution Plan (alpha-friendly but staged)
1. **Ship resource-neutral contracts**
   - RPC surfaces/DTOs now live under `resourcelease::*` (e.g., `ResourceLeaseEnqueueRequest`) with table-specific names removed.
   - Payloads require the `ResourceType`/`ResourceId` identity pair so resources are unambiguous.
   - Namespace/table fields have been removed entirely to avoid conflicting semantics.
2. **Productionize replication/deterministic stores**
   - Durable `IResourceLeaseReplicator` implementations now ship in-box: `SqliteResourceLeaseReplicator`, `GrpcResourceLeaseReplicator`, and `ObjectStorageResourceLeaseReplicator`.
   - `SqliteDeterministicStateStore` and `FileSystemDeterministicStateStore` provide hardened `IDeterministicStateStore` adapters. Wire them through `ResourceLeaseDeterministicOptions` when deterministic capture is enabled.
   - Configuration remains opt-in so clusters can switch from the in-memory defaults gradually.
3. **Expose mesh diagnostics + tooling**
   - Add health/metrics endpoints (peer health snapshots, backpressure state, replication checkpoints) and CLI/admin tools for drain/restore/lease inspection.
   - Provide scripts/operators for zero-downtime deployment/upgrade of the dispatcher component.
4. **Ship reusable backpressure + routing hooks**
   - Publish reference implementations of `IResourceLeaseBackpressureListener` that integrate with RateLimitingMiddleware, traffic shutters, or orchestrators.
   - Add helper APIs for sharding/routing so services can segment queues without bespoke code.
5. **Expand observability and governance integrations**
   - Standardize OTLP metrics/traces/log formats; ship dashboard + alert packs; supply replication sinks that feed lineage/governance systems out-of-the-box.
6. **Automate chaos/failure validation**
   - Create integration/chaos suites that kill peers, partition networks, corrupt replication logs, and assert deterministic recovery. Run them in CI/CD before releases.
7. **Complete the resource-lease migration**
   - Remove any remaining table-specific terminology/code paths.

### TODO / Implementation Backlog
1. **Mesh Deployment Guide**
   - Document step-by-step instructions for running ResourceLease dispatcher + middleware stack on every metadata node (install, config, health endpoints).
2. **Shared Replication Hub**
   - Harden the new durable replicators with operational guides (backup/restore, schema migrations) and sample configurations that wire dispatcher options to SQLite/object-store/gRPC hubs in single- and multi-region layouts.
3. **Deterministic Store Providers**
   - Ship plug-ins for common state stores (SQL, Cosmos DB, Redis) implementing `IDeterministicStateStore`, plus guidance for effect-id versioning policies.
4. **Peer Chooser Integrations**
   - Expose `PeerLeaseHealthTracker` snapshots via diagnostics endpoints and feed them into existing peer-chooser implementations in `Core.Peers`.
   - Add unit/integration tests covering unhealthy peer eviction and reactivation after heartbeat.
5. **Backpressure Hooks**
   - Implement sample `IResourceLeaseBackpressureListener` adapters (e.g., toggle RateLimitingMiddleware, emit control-plane events) and document integration points.
6. **Sharding Strategy**
   - Author guidance (and optional helper utilities) for running multiple ResourceLease queues per resource type or other lease identity boundary, including how to route payloads and aggregate replication streams.
7. **Mesh Health Dashboard**
   - Define standard metrics/OTLP spans for lease depth, replication lag, peer health, and backpressure, plus dashboards/alerts for mesh operators.
8. **Failure Drills**
   - Create playbooks + automated tests that simulate peer loss, partition, and recovery to validate that replication and deterministic replay avoid split-brain.
9. **Resource-Lease Adoption Kit**
   - Provide migration docs + code mods so early adopters can update quickly from the deprecated `tablelease::*` contracts.
   - Ship analyzer/linting guidance that flags usage of removed namespace/table fields in downstream payload builders.

Tracking these TODO items will take the existing ResourceLease + health + replication foundation and harden it into a full OmniRelay RPC Mesh. Each bullet is actionable with pointers to the relevant code paths for implementation.
