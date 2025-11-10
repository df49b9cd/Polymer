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
   - Use the new durable replicators when you need persistence beyond process memory: `SqliteResourceLeaseReplicator` (`OmniRelay.ResourceLeaseReplicator.Sqlite`), `ObjectStorageResourceLeaseReplicator` (`OmniRelay.ResourceLeaseReplicator.ObjectStorage`), or `GrpcResourceLeaseReplicator` (`OmniRelay.ResourceLeaseReplicator.Grpc`).
   - Optionally add `DeterministicResourceLeaseCoordinator` (via `ResourceLeaseDeterministicOptions`) so the same ordered events are persisted in an effect store for replay.
   - Harden deterministic capture by swapping in the production adapters: `SqliteDeterministicStateStore` (from the SQLite package) for lightweight SQL durability and `FileSystemDeterministicStateStore` (from the object-storage package) for human-inspectable JSON blobs on disk. Both satisfy `IDeterministicStateStore` and drop in without touching dispatcher code.
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
   - Durable `IResourceLeaseReplicator` implementations now live in dedicated packages (`OmniRelay.ResourceLeaseReplicator.Sqlite`, `.Grpc`, `.ObjectStorage`) so services can adopt only the transports they need.
   - `SqliteDeterministicStateStore` and `FileSystemDeterministicStateStore` ship with those packages and provide hardened `IDeterministicStateStore` adapters. Wire them through `ResourceLeaseDeterministicOptions` when deterministic capture is enabled.
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
   - See the “ResourceLease Mesh Deployment Guide” section below for the full checklist.
2. **Shared Replication Hub**
   - Harden the new durable replicators with operational guides (backup/restore, schema migrations) and sample configurations that wire dispatcher options to SQLite/object-store/gRPC hubs in single- and multi-region layouts.
   - (See the “Shared Replication Hub” section below for walk-throughs and configuration snippets.)
3. **Deterministic Store Providers**
   - Ship plug-ins for common state stores (SQL, Cosmos DB, Redis) implementing `IDeterministicStateStore`, plus guidance for effect-id versioning policies.
   - See “Deterministic Store Providers” below for current adapters and guidance.
4. **Peer Chooser Integrations**
   - Expose `PeerLeaseHealthTracker` snapshots via diagnostics endpoints and feed them into existing peer-chooser implementations in `Core.Peers`.
   - Add unit/integration tests covering unhealthy peer eviction and reactivation after heartbeat.
   - See “Peer Chooser Integrations” below for recommended diagnostics and test coverage.
5. **Backpressure Hooks**
   - Leverage the new `BackpressureAwareRateLimiter`, `RateLimitingBackpressureListener`, and `ResourceLeaseBackpressureDiagnosticsListener` helpers (see “Backpressure Hooks” below) so nodes automatically shed traffic and publish control-plane signals whenever SafeTaskQueue backpressure toggles.
6. **Sharding Strategy**
   - Use the new sharding guidance (see “Sharding Strategy” below) plus helpers like `ShardedResourceLeaseReplicator` and `CompositeResourceLeaseReplicator` to fan out work across multiple ResourceLease namespaces while keeping replication streams aggregated with shard metadata.
7. **Mesh Health Dashboard**
   - Wire the standard metrics (lease depth, replication lag, peer health, backpressure) and OTLP spans described in “Mesh Health Dashboard” so operators can triage mesh hot spots in seconds with shared dashboards + alerts.
8. **Failure Drills**
   - Create playbooks + automated tests that simulate peer loss, partition, and recovery to validate that replication and deterministic replay avoid split-brain.
9. **Resource-Lease Adoption Kit**
   - Provide migration docs + code mods so early adopters can update quickly from the deprecated `tablelease::*` contracts.
   - Ship analyzer/linting guidance that flags usage of removed namespace/table fields in downstream payload builders.

Tracking these TODO items will take the existing ResourceLease + health + replication foundation and harden it into a full OmniRelay RPC Mesh. Each bullet is actionable with pointers to the relevant code paths for implementation.

### ResourceLease Mesh Deployment Guide
1. **Install OmniRelay + replicator packages**
   - Add `OmniRelay`, `OmniRelay.Configuration`, and the replicators you need (`OmniRelay.ResourceLeaseReplicator.Sqlite`, `.Grpc`, `.ObjectStorage`) to every metadata node.
   - Ensure the host has .NET 8+ runtimes, OpenTelemetry exporters, and any native dependencies (for example `libsqlite3`).
2. **Configure dispatcher + middleware**
   - Register `ResourceLeaseDispatcherComponent` inside your DI container, wiring `PrincipalBindingMiddleware`, health trackers, and transport codecs (HTTP/gRPC) just like other OmniRelay dispatchers.
   - Provide a shared `PeerLeaseHealthTracker`, SafeTaskQueue options (capacity, watermarks, lease duration), and deterministic options if the node needs replay guarantees.
3. **Wire durable replication (optional but recommended)**
   - Choose one or more replicator options:
     - `SqliteResourceLeaseReplicator` for local durability (point it at a writable path and back up `.db` files).
     - `GrpcResourceLeaseReplicator` when you have a remote replication hub; configure TLS + credentials via `GrpcChannelOptions`.
     - `ObjectStorageResourceLeaseReplicator` for blob-based audit streams; supply an `IResourceLeaseObjectStore` implementation for your cloud provider.
   - Attach sinks (governance feeds, metrics) via `IResourceLeaseReplicationSink` to observe the ordered log.
4. **Enable deterministic capture where necessary**
   - Plug a durable `IDeterministicStateStore` (`SqliteDeterministicStateStore` or `FileSystemDeterministicStateStore`) into `ResourceLeaseDeterministicOptions`.
   - Define `ChangeId`, version windows, and effect-id format so replay boundaries are explicit during rollouts.
5. **Expose health + diagnostics**
   - Publish dispatcher health endpoints (pending depth, active leases, backpressure state, replication checkpoints) through your existing HTTP/gRPC health probes.
   - Surface `PeerLeaseHealthTracker.Snapshot()` via diagnostics APIs so operators can inspect peer eligibility and gossip metadata.
   - Ensure transport-level probes (Kubernetes `readiness`/`liveness`) call the OmniRelay health endpoints.
6. **Secure transports + principals**
   - Require mTLS or JWT auth on every inbound transport; configure `PrincipalBindingMiddleware` headers (`x-client-principal`, thumbprints) and verify that replication metadata includes `rpc.principal`.
   - Apply role-based access controls to outbound `resourcelease::*` calls (e.g., default deny for non-mesh callers).
7. **Operationalize**
   - Automate deployment via your IaC/CI system: install packages, publish config files (queue options, replicator connection strings), run `dotnet publish`, and copy binaries to each node.
   - Include runbooks for scaling queues, rotating deterministic state stores, bootstrapping new nodes (restore replication DBs), and draining work via `resourcelease::drain`.
   - Monitor `omnirelay.resourcelease.*` metrics plus replicator lag; add alerts for stuck pending counts, backpressure toggles, or missing heartbeats.

### Shared Replication Hub
1. **Choose a hub topology**
   - **Single-node durability**: run `SqliteResourceLeaseReplicator` on every metadata node. Pros: zero external dependency, fast local writes. Cons: backups must pull `.db` files from each node; cross-node replay requires replaying from multiple logs.
   - **Centralized hub**: deploy `GrpcResourceLeaseReplicator` behind a load-balanced service (per region). All dispatchers stream events to it; the hub persists to durable storage (another SQLite instance, PostgreSQL, or a log broker) and fans out to sinks. Pros: single source of truth; simpler lineage feeds. Cons: requires HA + backpressure control on the hub.
   - **Object-store audit**: `ObjectStorageResourceLeaseReplicator` emits immutable JSON blobs to S3/GCS/Azure Blob. Pros: cheap retention, easy analytics; Cons: higher latency, eventual consistency.
2. **Backup + restore guidance**
   - **SQLite replicator**:
     - Enable WAL mode (`PRAGMA journal_mode = wal`) via connection string for better write concurrency.
     - Schedule filesystem snapshots (or `sqlite3 .backup`) per node; compress and copy to off-site storage.
     - Restore by copying the `.db` file back and restarting the node; the replicator resumes from the highest `sequence_number`.
   - **gRPC hub**:
     - Host the hub inside a managed cluster (Kubernetes, Service Fabric) and attach a persistent volume for the hub’s own SQLite/log store.
     - Take periodic backups of the hub database and replicate it cross-region.
     - Document schema migrations (e.g., adding columns) by shipping SQL scripts with each release; apply using rolling upgrades while the hub is in maintenance mode.
   - **Object-store**:
     - Version object keys (`resourcelease/region/{seq:D20}.json`) and replicate buckets across regions (S3 Cross-Region Replication, GCS Turbo Replication).
     - For restores, list keys since the last checkpoint and stream them into a replay pipeline that rehydrates SafeTaskQueue items or deterministic logs.
3. **Sample configurations**
   - **SQLite (single-node)**
     ```csharp
     services.AddSingleton<IResourceLeaseReplicator>(_ =>
         new SqliteResourceLeaseReplicator(
             connectionString: "Data Source=/var/lib/omnirelay/leases.db;Cache=Shared",
             tableName: "LeaseEvents",
             sinks: new IResourceLeaseReplicationSink[] { governanceSink }));
     ```
   - **gRPC hub (region-scoped)**
     ```csharp
     var channel = GrpcChannel.ForAddress("https://replicator.us-east.example.com",
         new GrpcChannelOptions { HttpHandler = tlsHandler });

     services.AddSingleton<IResourceLeaseReplicator>(_ =>
         new GrpcResourceLeaseReplicator(
             new GrpcResourceLeaseReplicator.ResourceLeaseReplicatorGrpcClient(channel),
             sinks: Array.Empty<IResourceLeaseReplicationSink>(),
             startingSequence: 0));
     ```
   - **Object storage (multi-region audit)**
     ```csharp
     services.AddSingleton<IResourceLeaseObjectStore>(
         new S3ResourceLeaseObjectStore(bucketName: "omnirelay-replication-us"));

     services.AddSingleton<IResourceLeaseReplicator>(sp =>
         new ObjectStorageResourceLeaseReplicator(sp.GetRequiredService<IResourceLeaseObjectStore>(),
             keyPrefix: "resourcelease/us-east/",
             sinks: new[] { telemetrySink }));
     ```
4. **Multi-region considerations**
   - Deploy region-specific replicators (e.g., gRPC hubs per region, object-store prefixes per region) and attach a global aggregation pipeline (Data Lake, Kafka) for analytics.
   - When cross-region failover is required, mirror deterministic stores plus replication logs so a standby region can catch up before taking leases.
   - Document RPO/RTO assumptions: e.g., “SQLite nodes keep 24h of events; object storage retains 30 days; gRPC hub replicates to standby every 5s.”

### Deterministic Store Providers
1. **Current adapters**
   - `SqliteDeterministicStateStore` (in `OmniRelay.ResourceLeaseReplicator.Sqlite`) persists deterministic records in a local SQLite database. Use it in tandem with the SQLite replicator or whenever each node needs a self-contained deterministic gate. Configure WAL mode and take filesystem snapshots for backups.
   - `FileSystemDeterministicStateStore` (in `OmniRelay.ResourceLeaseReplicator.ObjectStorage`) writes JSON blobs per effect id. Use it for development, air-gapped deployments, or as a base class for S3/Azure blob adapters (simply override the file operations with bucket operations).
2. **Extending to cloud services**
   - **Cosmos DB**: implement `IDeterministicStateStore` using `Container.UpsertItemAsync` and conditional `PatchItemAsync` for `TryAdd`. Store `DeterministicRecord` payloads in base64 and index by `key`. Use TTL policies for old records.
   - **Redis**: back records with hashes where field names are `key` and values are the serialized record. Use Lua scripts that perform `EXISTS` checks + `SET` atomically to implement `TryAdd`.
   - **SQL Server/PostgreSQL**: copy the SQLite schema and swap `ON CONFLICT DO UPDATE` with the dialect-specific UPSERT syntax.
3. **Effect-id + version policies**
   - Keep `ChangeId` scoped to a dispatcher/region pair (e.g., `resourcelease.mesh.us-east`). Increment the ID or bump the version window whenever you change payload schema or deterministic semantics.
   - Use deterministic effect id factories such as `"{changeId}/seq/{sequenceNumber}"` or `"{changeId}/resource/{payload.ResourceId}/seq/{sequenceNumber}"` if you need per-resource replay.
   - During migrations, run dual writes: capture under `changeId.v1` and `changeId.v2` until all nodes are upgraded, then toggle the dispatcher to the new ID.
4. **Operations & monitoring**
   - Instrument `TryAdd` success/failure counts, read/write latency, and storage utilization per store. Unexpected `TryAdd=false` spikes usually indicate duplicate replication events or version mismatches.
   - Include deterministic stores in your backup story (same cadence as replication DBs). To restore, replay the replication log through the new store and rebuild deterministic gates before resuming leases.

### Peer Chooser Integrations
1. **Surface lease health via diagnostics**
   - When the diagnostics control plane is enabled and at least one `IPeerHealthSnapshotProvider` is registered, OmniRelay automatically exposes `/omnirelay/control/lease-health`. The endpoint returns a `PeerLeaseHealthDiagnostics` payload with per-peer snapshots plus summary counts (`eligible`, `unhealthy`, `pendingReassignments`) for dashboards.
   - Hosting your own control surface? Reuse the same helper so payloads stay consistent:
     ```csharp
     app.MapGet("/diagnostics/lease-health", (PeerLeaseHealthTracker tracker) =>
         Results.Json(PeerLeaseHealthDiagnostics.FromSnapshots(tracker.Snapshot())));
     ```
   - Annotate each snapshot with node metadata (region, build SHA) so fleet-wide dashboards can correlate unhealthy peers with deployments.
2. **Feed choosers with live data**
   - Update peer-chooser constructors (e.g., `FewestPendingPeerChooser`, `RoundRobinPeerChooser`) to accept a `PeerLeaseHealthTracker` or `IPeerHealthSnapshotProvider`. Before selecting a peer, call `IsPeerEligible(peerId)` to skip nodes without recent heartbeats.
   - Maintain a background task that subscribes to the diagnostics endpoint across the fleet and caches snapshots inside existing routing components (e.g., `PeerListCoordinator`). This allows choosers embedded in other services (gateways, orchestrators) to reuse the same health view.
3. **Testing guidance**
   - **Unit tests**: Simulate heartbeat/eviction flows by creating a `PeerLeaseHealthTracker`, recording heartbeats, failures, and requeues, then asserting that choosers skip unhealthy peers until a fresh heartbeat arrives.
   - **Integration tests**: Spin up multiple dispatcher instances with short heartbeat grace periods. Kill one node (or stop sending heartbeats) and assert that:
     1. Diagnostics endpoint shows the peer as `IsHealthy=false`.
     2. Peer chooser stops routing work to that peer.
     3. After resuming heartbeats, the peer is eligible again.
   - Run chaos-style tests that randomly drop heartbeats or inject duplicate lease assignments, validating that replication events + health tracker stay consistent.
4. **Operational hooks**
   - Expose derived metrics (`omnirelay.peer.unhealthy`, `omnirelay.peer.evictions`) so SREs can spot cluster-wide issues quickly.
   - Provide CLI/UX tools that query the diagnostics endpoint and show a ranked list of peers (`pending leases`, `last heartbeat`, `disconnect reason`) to speed up incident response.

### Backpressure Hooks
1. **Throttling via RateLimitingMiddleware**
   - `BackpressureAwareRateLimiter` and `RateLimitingBackpressureListener` (both under `src/OmniRelay/Dispatcher/ResourceLeaseBackpressureListeners.cs`) bridge SafeTaskQueue signals into OmniRelay’s `RateLimitingMiddleware`.
   - Create two limiters: one for steady-state traffic and one for backpressure mode (for example, a `ConcurrencyLimiter` that only allows a handful of concurrent RPCs). Register the selector with the middleware and the listener with the dispatcher:
     ```csharp
     var limiterGate = new BackpressureAwareRateLimiter(
         normalLimiter: new ConcurrencyLimiter(new() { PermitLimit = 512 }),
         backpressureLimiter: new ConcurrencyLimiter(new() { PermitLimit = 32 }));

     services.AddSingleton(limiterGate);
     services.AddSingleton<IResourceLeaseBackpressureListener>(sp =>
         new RateLimitingBackpressureListener(
             limiterGate,
             sp.GetRequiredService<ILogger<RateLimitingBackpressureListener>>()));

     dispatcherOptions.AddMiddleware(new RateLimitingMiddleware(new RateLimitingOptions
     {
         LimiterSelector = limiterGate.SelectLimiter
     }));
     ```
   - Whenever the dispatcher toggles backpressure, the listener flips the gate so every inbound/outbound RPC automatically slows down. Operators can swap in other `RateLimiter` implementations (token bucket, sliding window) without touching the dispatcher.
2. **Control-plane and streaming diagnostics**
   - `ResourceLeaseBackpressureDiagnosticsListener` captures the latest `ResourceLeaseBackpressureSignal` and exposes a bounded `ChannelReader` so HTTP/gRPC endpoints or CLI tools can stream transitions.
   - Register it alongside the dispatcher and map lightweight endpoints:
     ```csharp
     services.AddSingleton<ResourceLeaseBackpressureDiagnosticsListener>();
     services.AddSingleton<IResourceLeaseBackpressureListener>(sp =>
         sp.GetRequiredService<ResourceLeaseBackpressureDiagnosticsListener>());

     app.MapGet("/omnirelay/control/backpressure", (ResourceLeaseBackpressureDiagnosticsListener listener) =>
         listener.Latest is { } latest ? Results.Json(latest) : Results.NoContent());
     app.MapGet("/omnirelay/control/backpressure/stream", async (HttpContext ctx, ResourceLeaseBackpressureDiagnosticsListener listener) =>
     {
         await foreach (var update in listener.ReadAllAsync(ctx.RequestAborted))
         {
             await ctx.Response.WriteAsJsonAsync(update, ctx.RequestAborted);
         }
     });
     ```
   - Use the streaming endpoint for dashboards, CLI “watch” commands, or to fan out signals to orchestrators that drain upstream gateways automatically.

### Sharding Strategy
1. **Choose shard boundaries and namespaces**
   - Divide the global lease workload by resource type, tenant, or hash buckets. Create a dedicated `ResourceLeaseDispatcherComponent` per shard with a unique namespace (for example `resourcelease.users`, `resourcelease.billing`, `resourcelease.hash.00`, etc.). Namespaces keep procedure names (`resourcelease.users::enqueue`) predictable for clients and allow you to scale individual shards independently.
   - Reuse shared services (PeerLeaseHealthTracker, deterministic coordinator factories, middleware) per process so shards observe the same peer health and deterministic policies.
2. **Route requests consistently**
   - For server-side routing (for example control-plane APIs that enqueue work), build a simple map from `ResourceType` or `ResourceId` patterns to shard namespaces. Populate `RequestMeta.ShardKey` (or the `Rpc-Shard-Key` header when proxying HTTP/gRPC calls) so downstream instrumentation and gateways know which shard handled the request.
   - When multiple services enqueue into the mesh, centralize the routing logic in a small helper—e.g., `string ResolveShard(ResourceLeaseItemPayload payload)` that returns the namespace or `ShardKey`. Clients then call the matching RPC (`resourcelease.{shard}::enqueue`) directly.
3. **Aggregate replication streams**
   - Tag every shard’s replication output with a consistent metadata key so downstream sinks can distinguish which queue produced an event. The `ShardedResourceLeaseReplicator` helper (in `ResourceLeaseShardingReplicators.cs`) wraps any replicator and injects `"shard.id" = "{yourShard}"` before forwarding:
     ```csharp
     var globalReplicator = new InMemoryResourceLeaseReplicator(new[] { auditSink, checkpointSink });

     var usersShard = new ResourceLeaseDispatcherComponent(dispatcher, new ResourceLeaseDispatcherOptions
     {
         Namespace = "resourcelease.users",
         Replicator = new ShardedResourceLeaseReplicator(globalReplicator, shardId: "users"),
         LeaseHealthTracker = sharedTracker,
         QueueOptions = usersQueueOptions
     });
     ```
   - Need to fan events out to multiple downstream hubs (for example, a per-region durable log plus a central observability sink)? Wrap them with `CompositeResourceLeaseReplicator` so each shard publishes once but multiple replicators receive the same ordered stream.
4. **Share monitoring + control planes**
   - Surface each shard’s backpressure/status through the same diagnostics runtime. Reuse the `ResourceLeaseBackpressureDiagnosticsListener` and include the shard id in every payload (`Metadata["shard.id"]`) so dashboards can highlight which queue is under pressure.
   - When draining or restoring shards independently, use the `Namespace`-qualified procedures to target a single queue without pausing the rest of the mesh. Document shard-specific RPO/RTO (e.g., `resourcelease.billing` might use SQLite durability while `resourcelease.ml-jobs` points at an object-store replicator).

### Mesh Health Dashboard
1. **Lease depth & backpressure**
   - Metrics: `omnirelay.resourcelease.pending`, `omnirelay.resourcelease.active`, and `omnirelay.resourcelease.backpressure.transitions` (all emitted by `ResourceLeaseMetrics`). Plot pending/active as gauges with shard/service tags and alert when pending stays above 80% of the high watermark or when transitions spike.
   - Add a per-shard SLO card: “Time spent under backpressure < 1% over 30m”. Trigger paging alerts when `backpressure.transitions` exceeds a baseline or when a shard’s pending histogram shows p95 > high watermark for more than two intervals.
2. **Peer health**
   - Metrics: `omnirelay.peer.lease.healthy`, `omnirelay.peer.lease.unhealthy`, `omnirelay.peer.lease.pending_reassignments` (observable gauges updated whenever `PeerLeaseHealthTracker.Snapshot()` runs) plus existing `omnirelay.peer.lease_assignments`, `omnirelay.peer.lease_disconnects`.
   - Dashboards: stacked bar showing healthy vs unhealthy peers per region/service; table with pending reassignments. Alerts: unhealthy peers > 0 for N minutes or pending reassignments keeps growing, indicating peers stuck mid-work.
3. **Replication lag & volume**
   - Metrics: `omnirelay.resourcelease.replication.events` counter and `omnirelay.resourcelease.replication.lag` histogram (recorded when checkpointing sinks apply events). Monitor p95/p99 lag per shard and raise alerts when lag exceeds lease timeout or drifts steadily upward.
   - Overlay shard.id and event type tags to distinguish enqueue spikes from drain/restore operations.
4. **Tracing + span tags**
   - `RpcTracingMiddleware` already emits spans via `ActivitySource("OmniRelay.Rpc")` with tags like `rpc.service`, `rpc.peer`, `rpc.shard_key`, and `rpc.principal`. Pair those with a custom `resourcelease.operation` tag (add via middleware when calling ResourceLease procedures) so distributed traces show queue wait vs execution time.
   - Recommended alert: “Lease RPC latency p95 > 1s with `rpc.shard_key=hash-03`” correlates with pending depth and replication lag charts to highlight hot shards.
5. **Dashboards & alerts to ship**
   - Overview board: per-shard pending/active gauges, healthy/unhealthy peer counts, replication lag heatmap, backpressure transition timeline.
   - Alert catalog: (a) Pending depth > 90% of high watermark for 5m, (b) Replication lag > lease duration for any shard, (c) Unhealthy peers >= 1 for 3m, (d) Backpressure toggling more than X times/hour (thrash indicator). Include runbooks referencing drain/restore, shard routing, and peer eviction procedures.
