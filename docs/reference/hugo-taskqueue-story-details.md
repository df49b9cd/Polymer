# Hugo TaskQueue Control Plane Stories (Detailed)

This document expands each TaskQueue control-plane work item with the depth required for engineering design reviews, backlog planning, and QA sign-off. The material builds on the dispatcher analysis in `docs/reference/hugo-taskqueue-enhancement-stories.md` and references OmniRelay sources where current behaviour lives.

---

## Story 1 – TaskQueue Backpressure Monitor & Limiter Integration

### Goal
- Provide a reusable `TaskQueueBackpressureMonitor` in Hugo so hosts can observe SafeTaskQueue depth, toggle throttling, and publish state changes without duplicating OmniRelay’s `ResourceLeaseBackpressure*` helpers (`src/OmniRelay/Dispatcher/ResourceLeaseBackpressure*.cs`).

### Scope
- Applies to the Hugo TaskQueue package and `Go` concurrency toolkit.
- Includes signal generation, diagnostic listeners, limiter selection, and wait helpers.
- Excludes tenant/business-specific admission control or auto-scaling policies.

### Requirements
1. `TaskQueueBackpressureMonitor` wraps `TaskQueue<T>`/`SafeTaskQueue<T>`, tracks pending count, and compares it against configurable high/low watermarks with optional cooldown windows.
2. Emit strongly typed `TaskQueueBackpressureSignal` records (timestamp, pending, high/low watermarks, queue identifiers) whenever the monitor transitions in/out of backpressure.
3. Provide `ITaskQueueBackpressureListener` plus default implementations:
   - `BackpressureAwareRateLimiter` analog exposing a `LimiterSelector` delegate to plug into OmniRelay middleware.
   - `TaskQueueBackpressureDiagnosticsListener` analog that stores the latest signal, keeps a bounded history buffer, and exposes `ChannelReader<TaskQueueBackpressureSignal>` via `ReadAllAsync`.
4. Expose `WaitForDrainingAsync(CancellationToken)` so enqueue paths can await relief without reimplementing loops.
5. Integrate with `GoDiagnostics`: counters for transitions, gauges for pending depth, histograms for duration spent under backpressure, tags for queue/shard.
6. Provide configuration object including queue name, high/low thresholds, cooldown, optional `TimeProvider`, and instrumentation toggles.
7. Offer thread-safe APIs; monitor must handle concurrent enqueue/lease/complete calls without locks.

### Deliverables
- New namespace `Hugo.TaskQueues.Backpressure`.
- XML docs and updates to `docs/reference/hugo-api-reference.md`.
- How-to guide (e.g., `docs/reference/taskqueue-backpressure.md`) showing adoption in OmniRelay dispatcher & general-purpose workers.
- Sample snippet hooking monitor + limiter into OmniRelay’s `RateLimitingMiddleware`.
- Unit, integration, and feature tests (see sections below).

### Acceptance Criteria
1. Monitor toggles state accurately, honoring cooldowns, across simulated workloads.
2. Middleware can plug the limiter selector and observe the same throttling behaviour as existing OmniRelay listener.
3. Diagnostics listener delivers ordered signals to HTTP/gRPC streaming endpoints without drops under stress (verified via load tests).
4. `GoDiagnostics` meters show `hugo.taskqueue.backpressure.active`, `pending`, `transitions`, `duration` with consistent tags.
5. OmniRelay migration spike confirms dispatcher can delete `ResourceLeaseBackpressure*` files and rely on Hugo monitors without regressions.

### References
- `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:490-520` (manual gate/wait logic).
- `src/OmniRelay/Dispatcher/ResourceLeaseBackpressure.cs` and `ResourceLeaseBackpressureListeners.cs`.
- `docs/reference/distributed-task-leasing.md:60-78` (listener registration + metrics).
- `samples/ResourceLease.MeshDemo/Program.cs` (control-plane endpoint publishing signals).

### Testing Strategy
- Blend deterministic unit tests (threshold math, cooldown transitions) with TaskQueue integration tests and end-to-end feature tests inside OmniRelay samples.
- Include stress tests for channel backlogs and limiter flips.
- Run automation across both `TaskQueue<T>` and `SafeTaskQueue<T>` implementations.

### Unit Test Scenarios
- Transition toggling when pending counts cross thresholds (both directions).
- Cooldown enforcement: ensure redundant notifications suppressed until cooldown elapses.
- `WaitForDrainingAsync` unblocks immediately if backpressure inactive, waits otherwise, respects cancellation token.
- Diagnostics listener ring buffer capacity enforcement and snapshot updates.
- Limiter selector switching between normal/backpressure limiters with expected reference equality.

### Integration Test Scenarios
- `SafeTaskQueue<T>` + monitor + limiter inside an in-memory dispatcher with concurrent producers/consumers.
- Diagnostics listener wired to ASP.NET Core endpoint streaming SSE/gRPC and verifying ordering/delivery.
- OmniRelay middleware integration: ensure RPC throughput throttles when queue pending count stays above threshold and recovers afterwards.

### Feature Test Scenarios
- Deploy `samples/ResourceLease.MeshDemo` with the Hugo monitor, run chaos workloads that spike/recover/backpressure repeatedly, and confirm dashboards show new `GoDiagnostics` signals.
- Canary flip between old OmniRelay listener and new Hugo monitor to compare latency, throughput, CPU.
- Auto-scaling automation observing monitor events to trigger scale-out/in during synthetic load tests.

---

## Story 2 – TaskQueue Replication Source & Deterministic Sink Helpers

### Goal
- Ship a Hugo-level `TaskQueueReplicationSource` that emits ordered replication events, checkpointing sinks, and deterministic coordinators so OmniRelay and other hosts can drop custom `ResourceLeaseReplication*` plumbing.

### Scope
- Covers replication event contracts, sources, sinks, deterministic capture, and instrumentation.
- Excludes persistence backends (callers provide `IDeterministicStateStore`, blob storage, etc.).

### Requirements
1. Define transport-neutral `TaskQueueReplicationEvent` record mirroring OmniRelay fields: sequence number, timestamp, ownership handle, peer ID, payload, error info, metadata.
2. Implement `TaskQueueReplicationSource` hooking queue lifecycle events (enqueue, lease, heartbeat, ack, requeue, drain) and emitting ordered events via `ChannelWriter<TaskQueueReplicationEvent>` / `IAsyncEnumerable`.
3. Provide `CheckpointingTaskQueueReplicationSink` with per-peer/global checkpoints stored in thread-safe dictionaries to ensure idempotent application.
4. Build deterministic helpers:
   - `TaskQueueDeterministicCoordinator` bridging events into `Hugo.DeterministicEffectStore`.
   - Serializer context resolver akin to `ResourceLeaseDeterministicOptions` (support default context, caller-supplied context, or `JsonSerializerOptions` factory).
5. Add instrumentation hooks: counters for `replication.events`, histogram for lag (event timestamp vs application time), gauge for checkpoint distance.
6. Support sharded replication: ability to wrap events with shard identifiers and fan out to multiple sinks.
7. Ensure ordering even under concurrent queue operations; use atomic sequence increments and buffered channels to preserve order.

### Deliverables
- Namespace `Hugo.TaskQueues.Replication`.
- API reference updates and new doc (e.g., `docs/reference/taskqueue-replication.md`) describing topology patterns.
- Sample integration showing OmniRelay dispatcher migration and a simple background service applying events to `IDeterministicStateStore`.
- Complete test suite per below.

### Acceptance Criteria
1. Events are emitted strictly in order with monotonically increasing sequence numbers.
2. Checkpointing sinks only apply unseen events and persist per-peer/global checkpoints correctly.
3. Deterministic coordinator captures payloads and replays effects in restart simulations without duplication.
4. Observability output matches OmniRelay’s current `omnirelay.resourcelease.replication.*` metrics.
5. Migration spike demonstrates OmniRelay removing `ResourceLeaseReplication*` and `ResourceLeaseDeterministic*` files while maintaining behaviour.

### References
- `src/OmniRelay/Dispatcher/ResourceLeaseReplication.cs` (event contract + checkpointing).
- `src/OmniRelay/Dispatcher/ResourceLeaseDeterministic.cs` (coordinator + serializer context).
- `docs/reference/distributed-task-leasing.md:22-78`.
- `docs/reference/deterministic-coordination.md`.

### Testing Strategy
- Unit tests for event creation, serialization, checkpoint math.
- Integration tests simulating queue workloads with restarts.
- Feature tests inside OmniRelay sample clusters to validate replication-driven scenarios (failover, auditing).

### Unit Test Scenarios
- `TaskQueueReplicationEvent.Create` increments sequence numbers using provided `TimeProvider`.
- Concurrent sink application updates per-peer checkpoints independently without races.
- Deterministic coordinator selects correct serializer context path based on options.
- Sharded replicator distributes events to each inner sink with shard metadata intact.

### Integration Test Scenarios
- In-memory queue + replication source feeding a test sink; verify ordering under parallel enqueue/lease operations.
- Restart simulation: persist checkpoints, stop sink, produce events while sink offline, restart and confirm replay resumes from last checkpoint.
- Deterministic coordinator wired to `InMemoryDeterministicStateStore` capturing/replaying events across process restarts.

### Feature Test Scenarios
- OmniRelay sample cluster using Hugo replication source; verify peers subscribe to stream over gRPC and maintain consistent lease state.
- Failover drill: terminate active lease owner mid-lease, ensure replication log + deterministic capture provide enough data for audit + replay.
- Metrics validation: compare replication event counters/lag histograms before and after migration.

---

## Story 3 – TaskQueue Observability & Diagnostics Package

### Goal
- Bundle TaskQueue-specific metrics, tracing, and diagnostics under Hugo so operators can enable standardized instrumentation (meters, ActivitySources, sampling utilities) instead of maintaining OmniRelay-only code paths.

### Scope
- Metrics, tracing, diagnostic listeners, and helper middleware for SafeTaskQueue-driven workflows.
- Integrates with .NET `IMeterFactory`, `ActivitySource`, OpenTelemetry exporters.
- Excludes vendor-specific collectors/exporter deployments.

### Requirements
1. Introduce `Hugo.TaskQueues.Diagnostics` with:
   - `TaskQueueMetricsOptions` controlling instrument groups, histogram buckets, tag enrichers.
   - Extension methods `AddTaskQueueDiagnostics`, `ConfigureTaskQueueMeters`.
2. Default meters/counters:
   - Pending depth, active leases, throughput, replication events, replication lag, backpressure transitions.
3. ActivitySource helpers + `UseRateLimitedSampling` shortcuts tailored for TaskQueue operations (build on `docs/reference/hugo-api-reference.md:160-163` guidance).
4. Middleware/adapters so `TaskQueueBackpressureMonitor` and `TaskQueueReplicationSource` auto-emit diagnostics when registered.
5. Diagnostics host sample (extend `samples/ResourceLease.MeshDemo`) exposing metrics endpoints, gRPC/HTTP streaming diagnostics, and CLI-friendly watch endpoints.
6. Provide reset/dispose helpers for tests mirroring `Diagnostics.Reset()` semantics.

### Deliverables
- Diagnostics namespace, extension methods, sample host, and docs (e.g., `docs/reference/taskqueue-diagnostics.md`).
- Telemetry dashboard guidance (Grafana/Kusto queries, recommended alerts).
- Automated tests below.

### Acceptance Criteria
1. `AddTaskQueueDiagnostics` registers meters/activity sources and returns disposable handles aligning with `GoDiagnostics` setup guidance.
2. Metrics capture required fields with consistent tag schema (service, queue, shard, region).
3. Activity sampling respects rate limits, integrates with .NET 10 sampling changes, and exposes `IDisposable` handles for runtime toggling.
4. Diagnostics endpoints stream signals that existing CLI/watch commands can consume without modification.
5. OmniRelay dashboards ingest new instruments without schema changes (documented rollout plan).

### References
- `docs/reference/hugo-api-reference.md:85-166`.
- `docs/reference/distributed-task-leasing.md:60-78`.
- `samples/ResourceLease.MeshDemo/Program.cs`.
- `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:520-638`.

### Testing Strategy
- Unit tests for instrument registration and sampling helpers.
- Integration tests hosting diagnostics-enabled workers, scraping metrics via `MeterListener` or OTLP exporters.
- Feature tests verifying CLI/control-plane integrations and dashboards.

### Unit Test Scenarios
- Register diagnostics, simulate backpressure transitions, assert counters/histograms increment with correct tags.
- `UseRateLimitedSampling` returns `IDisposable` and toggles sampling decisions as configured.
- Reset helpers dispose meters/activity sources to avoid cross-test contamination.

### Integration Test Scenarios
- Host `TaskQueueBackpressureMonitor` + `TaskQueueReplicationSource` with diagnostics enabled, run workloads, and capture metrics via `MeterListener` validating names/values.
- Activity sampling with OpenTelemetry exporter ensuring spans respect max-per-interval quotas.
- Diagnostics host sample exposing `/diagnostics/taskqueue`; SSE/gRPC clients consume stream successfully.

### Feature Test Scenarios
- Containerized OmniRelay deployment enabling diagnostics package; run hyperscale load and compare dashboards before/after enabling new metrics.
- Failure drills (queue jam, replication lag spike) causing alerts to fire via new metrics/Activity traces.
- CLI `omnirelay control watch backpressure` continues to function against new diagnostics endpoints without code changes.

---

## Cross-Cutting Considerations

| Area | Notes |
| --- | --- |
| Versioning | Release Backpressure/Replication/Diagnostics packages under a single minor release to simplify adoption. |
| Migration Plan | Incrementally wire OmniRelay dispatcher to new APIs behind feature flags; document rollback path. |
| Documentation | Update quickstart and distributed task leasing references once migration completes. |
| Tooling | Extend analyzers/lint rules to encourage use of new packages instead of bespoke implementations. |
