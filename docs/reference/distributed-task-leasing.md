## ResourceLease dispatcher component

`ResourceLeaseDispatcherComponent` (under `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs`) hosts a dedicated `TaskQueue<ResourceLeaseWorkItem>` behind Hugo’s `SafeTaskQueueWrapper<T>` so every metadata node can expose the shared resource-lease surface. The dispatcher always registers procedures in the `resourcelease` namespace (override via `ResourceLeaseDispatcherOptions.Namespace` if needed):

| Procedure | Description |
| --- | --- |
| `resourcelease::enqueue` | Accepts `ResourceLeaseEnqueueRequest` and appends the payload to the SafeTaskQueue. Returns `ResourceLeaseEnqueueResponse` containing live pending/active stats. |
| `resourcelease::lease` | Blocks until a lease is granted and returns `ResourceLeaseLeaseResponse` with the payload, `SequenceId`, `Attempt`, and a `ResourceLeaseOwnershipHandle` (token = SequenceId, Attempt, LeaseId). Tokens back all ack operations. |
| `resourcelease::complete` | Completes the outstanding lease referenced by `ResourceLeaseOwnershipHandle`. |
| `resourcelease::heartbeat` | Issues a heartbeat for the referenced lease without handing work to another node. |
| `resourcelease::fail` | Fails or requeues the referenced lease with a structured `Error` derived from the request. |
| `resourcelease::drain` | Calls `TaskQueue<T>.DrainPendingItemsAsync` and returns serialized `ResourceLeasePendingItemDto` records (payload + attempt/dead-letter metadata). |
| `resourcelease::restore` | Rehydrates drained items by constructing `TaskQueuePendingItem<T>` instances and invoking `RestorePendingItemsAsync`. |

The DTOs live next to the component (`ResourceLeaseItemPayload`, `ResourceLeaseOwnershipHandle`, `ResourceLeaseErrorInfo`, etc.) and reuse the dispatcher JSON helpers. Payloads always carry the resource identity pair (`ResourceType`, `ResourceId`) plus optional `PartitionKey`, `Attributes`, and opaque `Body` bytes; namespace/table fields have been removed entirely so other domains do not need to translate through table semantics. Pending/restore flows preserve `SequenceId`, `Attempt`, and prior ownership tokens so another node can replay the same work with its original fencing metadata.

Use `ResourceLeaseDispatcherOptions.QueueOptions` to align lease duration, heartbeat cadence, capacity, and backpressure thresholds with your SafeTaskQueue settings.

### Peer health + membership gossip

- `ResourceLeaseDispatcherOptions.LeaseHealthTracker` accepts a shared `PeerLeaseHealthTracker` (under `Core.Peers`). When supplied, the dispatcher emits lease assignments, heartbeats, disconnects, and requeue signals into the tracker so peer choosers can filter unhealthy owners.
- `ResourceLeaseLeaseRequest` accepts an optional `peerId`. If callers omit it, the dispatcher falls back to `RequestMeta.Caller` or the `x-peer-id` header. The ID is echoed on `ResourceLeaseLeaseResponse.OwnerPeerId` and on replication events.
- Heartbeats (`resourcelease::heartbeat`), completes, and fails record health events for the owning peer using SafeTaskQueue ownership tokens. When a peer fails a lease without requeueing, `PeerLeaseHealthTracker.RecordDisconnect` fires and the corresponding `PeerListCoordinator` stops selecting that peer until a fresh heartbeat arrives.
- Use the same tracker instance—or any `IPeerHealthSnapshotProvider` implementation—when constructing peer choosers (for example `new RoundRobinPeerChooser(peers, leaseHealthTracker)` or `new RoundRobinPeerChooser(peers, customProvider)`) so `PeerListCoordinator` can call `IsPeerEligible` before issuing a lease. The shared provider also feeds `PeerListCoordinator.LeaseHealth` and the `/omnirelay/control/lease-health` diagnostics endpoint, keeping routing and observability aligned.

### Ordered replication stream

- `ResourceLeaseDispatcherOptions.Replicator` accepts an `IResourceLeaseReplicator` that sequences every enqueue/lease/heartbeat/complete/fail/drain/restore mutation and fans the ordered log to subscribers. Each `ResourceLeaseReplicationEvent` carries a monotonic `SequenceNumber`, ownership token, optional payload/error info, and queue depth metadata so every node can deterministically replay the same state.
- `ResourceLeaseReplicationEventType` enumerates the canonical changes (`Enqueue`, `LeaseGranted`, `Heartbeat`, `Completed`, `Failed`, `DrainSnapshot`, `RestoreSnapshot`). Events are published after the SafeTaskQueue operation succeeds, ensuring replicating nodes never observe speculative mutations.
- `InMemoryResourceLeaseReplicator` ships as a default hub: it increments the sequence number, timestamps the event, and delivers it to registered `IResourceLeaseReplicationSink` instances. `CheckpointingResourceLeaseReplicationSink` provides deduplication by discarding events with sequence numbers at or below the last applied checkpoint, which survives retries and out-of-order deliveries in streaming transports.
- Durable implementations are available as dedicated packages so you can pull only the transports you need:
  - `SqliteResourceLeaseReplicator` (package `OmniRelay.ResourceLeaseReplicator.Sqlite`) persists each event inside a SQLite database before fanning out to sinks. It automatically creates the table (customise via options) and resumes sequence numbers from the stored log after restarts.
  - `ObjectStorageResourceLeaseReplicator` (package `OmniRelay.ResourceLeaseReplicator.ObjectStorage`) emits immutable JSON blobs to any `IResourceLeaseObjectStore`. The built-in `FileSystemResourceLeaseObjectStore` targets local disks; implement S3/GCS/Azure equivalents by implementing the interface in that package.
  - `GrpcResourceLeaseReplicator` (package `OmniRelay.ResourceLeaseReplicator.Grpc`) forwards events to a remote `ResourceLeaseReplicatorGrpc` endpoint so operators can centralise the log while still running local sinks. The package ships the `ResourceLeaseReplication.proto` contract and generated client.
- Downstream nodes can attach sinks that write to a Raft log, emit gRPC/HTTP streaming updates, or feed a `DeterministicGate` workflow. Because every event includes the `ResourceLeaseOwnershipHandle` (sequence + attempt + leaseId) the same fencing semantics described in `docs/reference/hugo-api-reference.md#task-queue-components` apply across replicas.

### Deterministic recovery tooling

- `ResourceLeaseDispatcherOptions.DeterministicCoordinator` (or the convenience `DeterministicOptions`) wires Hugo’s `VersionGate`, `DeterministicGate`, and `DeterministicEffectStore` directly into the replication stream. Every `ResourceLeaseReplicationEvent` is captured under a stable effect id (`{changeId}/seq/{sequenceNumber}` by default), so if a node fails mid-flight the effect store guarantees the same sequence is replayed exactly once when it resumes.
- `ResourceLeaseDeterministicOptions` accepts any `IDeterministicStateStore`, letting you persist coordination metadata in SQL, Cosmos DB, Redis, etc. Override `ChangeId`, `MinVersion`, `MaxVersion`, or `EffectIdFactory` to align with existing rollout/version policies.
- When the dispatcher publishes an event it flows through the deterministic coordinator before returning to callers. Combined with the replication log this provides a single source of truth for lease state, replay-safe compensations, and auditable history without building a separate workflow engine.
- Use the hardened adapters when wiring deterministic capture in production:
  - `SqliteDeterministicStateStore` (package `OmniRelay.ResourceLeaseReplicator.Sqlite`) stores state in a SQLite table (`DeterministicStateStore` by default) using optimistic inserts for `TryAdd`. Ideal for embedded deployments or single-node control planes.
  - `FileSystemDeterministicStateStore` (package `OmniRelay.ResourceLeaseReplicator.ObjectStorage`) writes JSON blobs (keyed by the SHA-256 hash) to the filesystem so diagnostics can be inspected with standard tooling. Because each record is immutable, deterministic replays tolerate process crashes or machine restarts.

### Backpressure + flow control

- `TaskQueueOptions.Backpressure` is always wired to the dispatcher. When the SafeTaskQueue crosses its configured `HighWatermark`, `resourcelease::enqueue` calls pause until the queue drains below the `LowWatermark`, preventing unbounded buffering inside OmniRelay transports.
- Register an `IResourceLeaseBackpressureListener` through `ResourceLeaseDispatcherOptions.BackpressureListener` to integrate the signal with upstream throttling (for example toggling `RateLimitingMiddleware` or nudging clients via side channels). Each callback receives a `ResourceLeaseBackpressureSignal` describing the active state, pending depth, observed timestamp, and configured watermarks.
- Sample adapters live in `src/OmniRelay/Dispatcher/ResourceLeaseBackpressureListeners.cs`:
  - `BackpressureAwareRateLimiter` + `RateLimitingBackpressureListener` toggle the limiter used by `RateLimitingMiddleware`. Wire the selector like this:
    ```csharp
    var limiterGate = new BackpressureAwareRateLimiter(
        normalLimiter: new ConcurrencyLimiter(new() { PermitLimit = 1024 }),
        backpressureLimiter: new ConcurrencyLimiter(new() { PermitLimit = 64 }));

    services.AddSingleton(limiterGate);
    services.AddSingleton<IResourceLeaseBackpressureListener>(sp =>
        new RateLimitingBackpressureListener(limiterGate, sp.GetRequiredService<ILogger<RateLimitingBackpressureListener>>()));

    dispatcherOptions.AddMiddleware(new RateLimitingMiddleware(new RateLimitingOptions
    {
        LimiterSelector = limiterGate.SelectLimiter
    }));
    ```
  - `ResourceLeaseBackpressureDiagnosticsListener` exposes the latest signal plus a `ChannelReader` so control-plane endpoints or SSE/gRPC streams can publish transitions:
    ```csharp
    services.AddSingleton<ResourceLeaseBackpressureDiagnosticsListener>();
    services.AddSingleton<IResourceLeaseBackpressureListener>(sp =>
        sp.GetRequiredService<ResourceLeaseBackpressureDiagnosticsListener>());

    app.MapGet("/omnirelay/control/backpressure", (ResourceLeaseBackpressureDiagnosticsListener listener) =>
        listener.Latest is { } latest ? Results.Json(latest) : Results.NoContent());
    ```
- `ResourceLeaseMetrics` emits `omnirelay.resourcelease.pending`, `omnirelay.resourcelease.active`, and `omnirelay.resourcelease.backpressure.transitions` so dashboards can visualize queue depth and backpressure churn over time.

### Sharding strategy

- Run multiple `ResourceLeaseDispatcherComponent` instances per process when you need to isolate workloads (per resource type, tenant, or hash bucket). Give each shard a unique namespace (e.g., `resourcelease.users`, `resourcelease.billing`) via `ResourceLeaseDispatcherOptions.Namespace`.
- Use `RequestMeta.ShardKey` (and the `Rpc-Shard-Key` transport header) to record which shard handled a request. Clients can compute the shard key from `ResourceType`, `ResourceId`, or any domain attribute, then call the matching namespace (`resourcelease.{shard}::enqueue`).
- Aggregate replication without losing shard identity by wrapping replicate hubs:
  ```csharp
  var sharedHub = new InMemoryResourceLeaseReplicator();

  var usersReplicator = new ShardedResourceLeaseReplicator(sharedHub, shardId: "users");
  var billingReplicator = new ShardedResourceLeaseReplicator(sharedHub, shardId: "billing");

  var composite = new CompositeResourceLeaseReplicator(new IResourceLeaseReplicator[]
  {
      usersReplicator,
      new ShardedResourceLeaseReplicator(otherHub, shardId: "users")
  });
  ```
  - `ShardedResourceLeaseReplicator` injects `Metadata["shard.id"]` before forwarding events so downstream sinks, deterministic stores, or analytics pipelines can group by shard.
  - `CompositeResourceLeaseReplicator` fans out every event to multiple replicators—use it when a shard must write to both a local durable log and a remote streaming hub.
- Monitor each shard through the same diagnostics runtime by reusing the listeners from “Backpressure Hooks.” Include the shard id in every payload (via the helper above or custom metadata) so dashboards can highlight hot partitions.

### Security & identity propagation

- Add `PrincipalBindingMiddleware` (see `src/OmniRelay/Core/Middleware/PrincipalBindingMiddleware.cs`) to the inbound pipeline before ResourceLease procedures to normalize the caller. The middleware inspects TLS headers such as `x-mtls-subject` / `x-mtls-thumbprint` or auth headers (`Authorization: Bearer ...`) and promotes the resolved identity into `RequestMeta.Caller` plus the `rpc.principal` metadata key. ResourceLease dispatch automatically uses that identity when populating `OwnerPeerId`, replication events, and gossip records.
- Configure `PrincipalBindingOptions` with the exact headers your gateways populate. For mTLS, terminate TLS at the OmniRelay inbound (or a trusted proxy) with `ClientCertificateMode = RequireCertificate` and mirror the subject/thumbprint into headers like `x-mtls-subject`. For bearer/JWT flows, run your existing auth middleware first, add an `x-client-principal` header, and let the binding middleware propagate it.
- Because the middleware copies thumbprints/tokens into metadata, replication logs, audit trails, and metrics inherit the same principal. Pair it with the deterministic log controls above to build full-chain forensic trails for every lease assignment, heartbeat, and failover.
