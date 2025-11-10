## TableLease dispatcher component

`TableLeaseDispatcherComponent` (under `src/OmniRelay/Dispatcher/TableLeaseDispatcher.cs`) hosts a dedicated `TaskQueue<TableLeaseWorkItem>` behind Hugo’s `SafeTaskQueueWrapper<T>` so every metadata node can expose a consistent lease surface. When constructed with a dispatcher it automatically registers the following JSON procedures (namespace defaults to `tablelease` and can be overridden via `TableLeaseDispatcherOptions.Namespace`):

| Procedure | Description |
| --- | --- |
| `tablelease::enqueue` | Accepts `TableLeaseEnqueueRequest` and appends the payload to the SafeTaskQueue. Returns `TableLeaseEnqueueResponse` with live pending/active stats. |
| `tablelease::lease` | Blocks until a lease is granted and returns `TableLeaseLeaseResponse` containing the payload, `SequenceId`, `Attempt`, and a `TableLeaseOwnershipHandle` (token = SequenceId, Attempt, LeaseId). Tokens back the ack operations below. |
| `tablelease::complete` | Completes the outstanding lease referenced by `TableLeaseOwnershipHandle`. |
| `tablelease::heartbeat` | Issues a heartbeat for the referenced lease without handing work to another node. |
| `tablelease::fail` | Fails or requeues the referenced lease with a structured `Error` derived from the request. |
| `tablelease::drain` | Calls `TaskQueue<T>.DrainPendingItemsAsync` and returns serialized `TableLeasePendingItemDto` records (payload + attempt/dead-letter metadata). |
| `tablelease::restore` | Rehydrates drained items by constructing `TaskQueuePendingItem<T>` instances and invoking `RestorePendingItemsAsync`. |

Each DTO lives alongside the component (`TableLeaseItemPayload`, `TableLeaseOwnershipHandle`, `TableLeaseErrorInfo`, etc.) and is encoded via the existing JSON dispatcher helpers. Pending/restore flows preserve `SequenceId`, `Attempt`, and prior ownership tokens so another node can replay the same work with its original fencing metadata.

Use `TableLeaseDispatcherOptions.QueueOptions` to align lease duration, heartbeat cadence, capacity, and backpressure thresholds with Lakeview’s SafeTaskQueue settings.

### Peer health + membership gossip

- `TableLeaseDispatcherOptions.LeaseHealthTracker` accepts a shared `PeerLeaseHealthTracker` (under `Core.Peers`). When supplied, the dispatcher emits lease assignments, heartbeats, disconnects, and requeue signals into the tracker so peer choosers can filter unhealthy owners.
- `TableLeaseLeaseRequest` now accepts an optional `peerId`. If callers omit it, the dispatcher falls back to `RequestMeta.Caller` or the `x-peer-id` header. The ID is echoed on `TableLeaseLeaseResponse.OwnerPeerId`.
- Heartbeats (`tablelease::heartbeat`), completes, and fails record health events for the owning peer using SafeTaskQueue ownership tokens. When a peer fails a lease without requeueing, `PeerLeaseHealthTracker.RecordDisconnect` fires and the corresponding `PeerListCoordinator` stops selecting that peer until a fresh heartbeat arrives.
- Use the same tracker instance when constructing peer choosers (for example `new RoundRobinPeerChooser(peers, leaseHealthTracker)`) so `PeerListCoordinator` can call `IsPeerEligible` before issuing a lease. `PeerListCoordinator.LeaseHealth` surfaces current `PeerLeaseHealthSnapshot` data for dispatcher introspection endpoints.

### Ordered replication stream

- `TableLeaseDispatcherOptions.Replicator` accepts an `ITableLeaseReplicator` that sequences every enqueue/lease/heartbeat/complete/fail/drain/restore mutation and fans the ordered log to subscribers. Each `TableLeaseReplicationEvent` carries the monotonic `SequenceNumber`, ownership token, optional payload/error info, and queue depth metadata so every metadata node can deterministically replay the same state.
- `TableLeaseReplicationEventType` enumerates the canonical changes (`Enqueue`, `LeaseGranted`, `Heartbeat`, `Completed`, `Failed`, `DrainSnapshot`, `RestoreSnapshot`). Events are published after the SafeTaskQueue operation succeeds, ensuring that replicating nodes never observe speculative mutations.
- `InMemoryTableLeaseReplicator` ships as a default hub: it increments the sequence number, timestamps the event, and delivers it to registered `ITableLeaseReplicationSink` instances. `CheckpointingTableLeaseReplicationSink` provides deduplication by discarding events with sequence numbers at or below the last applied checkpoint, which is enough to survive retries and out-of-order deliveries in streaming transports.
- Downstream nodes can attach sinks that write to a Raft log, emit gRPC/HTTP streaming updates, or feed a `DeterministicGate` workflow. Because every event includes the `TableLeaseOwnershipHandle` (sequence + attempt + leaseId) the same fencing semantics described in `docs/reference/hugo-api-reference.md#task-queue-components` apply across replicas.

### Deterministic recovery tooling

- `TableLeaseDispatcherOptions.DeterministicCoordinator` (or the convenience `DeterministicOptions`) wires Hugo’s `VersionGate`, `DeterministicGate`, and `DeterministicEffectStore` directly into the replication stream. Every `TableLeaseReplicationEvent` is captured under a stable effect id (`{changeId}/seq/{sequenceNumber}` by default), so if a node fails mid-flight the effect store guarantees the same sequence is replayed exactly once when it resumes.
- `TableLeaseDeterministicOptions` accepts any `IDeterministicStateStore`, letting Lakeview persist coordination metadata in SQL, Cosmos DB, Redis, etc. Override `ChangeId`, `MinVersion`, `MaxVersion`, or `EffectIdFactory` to align with existing rollout/version policies.
- When the dispatcher publishes an event it now flows through the deterministic coordinator before returning to callers. Combined with the replication log this gives Lakeview a single source of truth for lease state, replay-safe compensations, and auditable history without building a separate workflow engine.

### Backpressure + flow control

- `TaskQueueOptions.Backpressure` is always wired to the dispatcher. When the SafeTaskQueue crosses its configured `HighWatermark`, `tablelease::enqueue` calls pause until the queue drains below the `LowWatermark`, preventing unbounded buffering inside OmniRelay transports.
- Register an `ITableLeaseBackpressureListener` through `TableLeaseDispatcherOptions.BackpressureListener` to integrate the signal with upstream throttling (for example toggling `RateLimitingMiddleware` or nudging clients via side channels). Each callback receives a `TableLeaseBackpressureSignal` describing the active state, pending depth, observed timestamp, and watermarks.
- `TableLeaseMetrics` now emits `omnirelay.tablelease.pending`, `omnirelay.tablelease.active`, and `omnirelay.tablelease.backpressure.transitions` so dashboards can visualize queue depth and backpressure churn over time.

### Security & identity propagation

- Add `PrincipalBindingMiddleware` (see `src/OmniRelay/Core/Middleware/PrincipalBindingMiddleware.cs`) to the inbound pipeline before TableLease procedures to normalize the caller. The middleware inspects TLS headers such as `x-mtls-subject` / `x-mtls-thumbprint` or auth headers (`Authorization: Bearer ...`) and promotes the resolved identity into `RequestMeta.Caller` plus the `rpc.principal` metadata key. TableLease dispatch then automatically uses that identity when populating `OwnerPeerId`, replication events, and gossip records.
- Configure `PrincipalBindingOptions` with the exact headers your gateways populate. For mTLS, terminate TLS at the OmniRelay inbound (or a trusted proxy) with `ClientCertificateMode = RequireCertificate` and mirror the subject/thumbprint into headers like `x-mtls-subject`. For bearer/JWT flows, run your existing auth middleware first, add an `x-client-principal` header, and let the binding middleware propagate it.
- Because the middleware copies thumbprints/tokens into metadata, replication logs, audit trails, and metrics inherit the same principal. Pair it with the deterministic log controls above to build full-chain forensic trails for every lease assignment, heartbeat, and failover.
