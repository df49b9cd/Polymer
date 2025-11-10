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
- Use the same tracker instance when constructing peer choosers (for example `new RoundRobinPeerChooser(peers, leaseHealthTracker)`) so `PeerListCoordinator` can call `IsPeerEligible` before issuing a lease. `PeerListCoordinator.LeaseHealth` surfaces current `PeerLeaseHealthSnapshot` data for dispatcher introspection endpoints.

### Ordered replication stream

- `ResourceLeaseDispatcherOptions.Replicator` accepts an `IResourceLeaseReplicator` that sequences every enqueue/lease/heartbeat/complete/fail/drain/restore mutation and fans the ordered log to subscribers. Each `ResourceLeaseReplicationEvent` carries a monotonic `SequenceNumber`, ownership token, optional payload/error info, and queue depth metadata so every node can deterministically replay the same state.
- `ResourceLeaseReplicationEventType` enumerates the canonical changes (`Enqueue`, `LeaseGranted`, `Heartbeat`, `Completed`, `Failed`, `DrainSnapshot`, `RestoreSnapshot`). Events are published after the SafeTaskQueue operation succeeds, ensuring replicating nodes never observe speculative mutations.
- `InMemoryResourceLeaseReplicator` ships as a default hub: it increments the sequence number, timestamps the event, and delivers it to registered `IResourceLeaseReplicationSink` instances. `CheckpointingResourceLeaseReplicationSink` provides deduplication by discarding events with sequence numbers at or below the last applied checkpoint, which survives retries and out-of-order deliveries in streaming transports.
- Durable implementations are available for production:
  - `SqliteResourceLeaseReplicator` persists each event inside a SQLite database before fanning out to sinks. It automatically creates the table (customise via options) and resumes sequence numbers from the stored log after restarts.
  - `ObjectStorageResourceLeaseReplicator` emits immutable JSON blobs to any `IResourceLeaseObjectStore` (the built-in `FileSystemResourceLeaseObjectStore` targets local disks; implement S3/GCS/Azure equivalents by implementing the interface).
  - `GrpcResourceLeaseReplicator` forwards events to a remote `ResourceLeaseReplicatorGrpc` endpoint so operators can centralise the log while still running local sinks. See `ResourceLeaseReplication.proto` for the contract.
- Downstream nodes can attach sinks that write to a Raft log, emit gRPC/HTTP streaming updates, or feed a `DeterministicGate` workflow. Because every event includes the `ResourceLeaseOwnershipHandle` (sequence + attempt + leaseId) the same fencing semantics described in `docs/reference/hugo-api-reference.md#task-queue-components` apply across replicas.

### Deterministic recovery tooling

- `ResourceLeaseDispatcherOptions.DeterministicCoordinator` (or the convenience `DeterministicOptions`) wires Hugo’s `VersionGate`, `DeterministicGate`, and `DeterministicEffectStore` directly into the replication stream. Every `ResourceLeaseReplicationEvent` is captured under a stable effect id (`{changeId}/seq/{sequenceNumber}` by default), so if a node fails mid-flight the effect store guarantees the same sequence is replayed exactly once when it resumes.
- `ResourceLeaseDeterministicOptions` accepts any `IDeterministicStateStore`, letting you persist coordination metadata in SQL, Cosmos DB, Redis, etc. Override `ChangeId`, `MinVersion`, `MaxVersion`, or `EffectIdFactory` to align with existing rollout/version policies.
- When the dispatcher publishes an event it flows through the deterministic coordinator before returning to callers. Combined with the replication log this provides a single source of truth for lease state, replay-safe compensations, and auditable history without building a separate workflow engine.
- Use the hardened adapters when wiring deterministic capture in production:
  - `SqliteDeterministicStateStore` stores state in a SQLite table (`DeterministicStateStore` by default) using optimistic inserts for `TryAdd`. Ideal for embedded deployments or single-node control planes.
  - `FileSystemDeterministicStateStore` writes JSON blobs (keyed by the SHA-256 hash) to the filesystem so diagnostics can be inspected with standard tooling. Because each record is immutable, deterministic replays tolerate process crashes or machine restarts.

### Backpressure + flow control

- `TaskQueueOptions.Backpressure` is always wired to the dispatcher. When the SafeTaskQueue crosses its configured `HighWatermark`, `resourcelease::enqueue` calls pause until the queue drains below the `LowWatermark`, preventing unbounded buffering inside OmniRelay transports.
- Register an `IResourceLeaseBackpressureListener` through `ResourceLeaseDispatcherOptions.BackpressureListener` to integrate the signal with upstream throttling (for example toggling `RateLimitingMiddleware` or nudging clients via side channels). Each callback receives a `ResourceLeaseBackpressureSignal` describing the active state, pending depth, observed timestamp, and configured watermarks.
- `ResourceLeaseMetrics` emits `omnirelay.resourcelease.pending`, `omnirelay.resourcelease.active`, and `omnirelay.resourcelease.backpressure.transitions` so dashboards can visualize queue depth and backpressure churn over time.

### Security & identity propagation

- Add `PrincipalBindingMiddleware` (see `src/OmniRelay/Core/Middleware/PrincipalBindingMiddleware.cs`) to the inbound pipeline before ResourceLease procedures to normalize the caller. The middleware inspects TLS headers such as `x-mtls-subject` / `x-mtls-thumbprint` or auth headers (`Authorization: Bearer ...`) and promotes the resolved identity into `RequestMeta.Caller` plus the `rpc.principal` metadata key. ResourceLease dispatch automatically uses that identity when populating `OwnerPeerId`, replication events, and gossip records.
- Configure `PrincipalBindingOptions` with the exact headers your gateways populate. For mTLS, terminate TLS at the OmniRelay inbound (or a trusted proxy) with `ClientCertificateMode = RequireCertificate` and mirror the subject/thumbprint into headers like `x-mtls-subject`. For bearer/JWT flows, run your existing auth middleware first, add an `x-client-principal` header, and let the binding middleware propagate it.
- Because the middleware copies thumbprints/tokens into metadata, replication logs, audit trails, and metrics inherit the same principal. Pair it with the deterministic log controls above to build full-chain forensic trails for every lease assignment, heartbeat, and failover.
