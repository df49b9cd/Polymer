# Hugo TaskQueue Control Plane Stories

This document breaks down three Hugo work items that emerged while examining how OmniRelay layers SafeTaskQueue-backed resource leasing, replication, and throttling in production (`src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:11-223`, `docs/reference/distributed-task-leasing.md:1-78`). Each story is structured with the artifacts engineering and QA teams need to plan, implement, and verify the enhancements before OmniRelay migrates from its bespoke dispatcher utilities.

## Story 1 – TaskQueue Backpressure Monitor & Limiter Integration

### Goal
- Provide a reusable `TaskQueueBackpressureMonitor` inside Hugo so hosts can observe SafeTaskQueue depth, toggle throttling, and publish state changes without duplicating OmniRelay’s `ResourceLeaseBackpressureSignal` + rate-limiter selector (`src/OmniRelay/Dispatcher/ResourceLeaseBackpressure.cs:4-26`, `src/OmniRelay/Dispatcher/ResourceLeaseBackpressureListeners.cs:8-150`).

### Scope
- Applies to Hugo’s TaskQueue package and the `Go` concurrency toolkit.
- Covers synchronous/asynchronous waiters, backpressure cooldown enforcement, and rate-limiter selection hooks.
- Excludes transport- or tenant-specific admission control; callers still choose how to act on signals.

### Requirements
1. Surface a `TaskQueueBackpressureMonitor` type that wraps a `SafeTaskQueue<T>` (or `TaskQueue<T>`) and tracks pending count against configurable high/low watermarks plus optional cooldown (parity with `ResourceLeaseDispatcherComponent` in `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:490-520`).
2. Emit strongly typed `TaskQueueBackpressureSignal` instances with timestamp, pending count, high/low watermark metadata, and monotonic transitions (mirroring OmniRelay’s signal contract).
3. Provide an `ITaskQueueBackpressureListener` interface + default implementations:
   - `BackpressureAwareRateLimiter` analog that swaps between two `RateLimiter` instances and exposes a `LimiterSelector` delegate for OmniRelay’s middleware (`src/OmniRelay/Dispatcher/ResourceLeaseBackpressureListeners.cs:8-80`).
   - `TaskQueueBackpressureDiagnosticsListener` analog with `ChannelReader<TaskQueueBackpressureSignal>` + latest snapshot for control-plane endpoints.
4. Expose a `WaitForDrainingAsync` helper (same semantics as `WaitForBackpressureAsync` in `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:500-512`) so enqueue paths can await signal clearance without rewriting loops.
5. Ensure monitor instances integrate with `GoDiagnostics` to record transitions, durations, and queue depth gauges, feeding OmniRelay dashboards currently built on `ResourceLeaseMetrics` (`docs/reference/distributed-task-leasing.md:60-78`).

### Deliverables
- New Hugo namespace (e.g., `Hugo.TaskQueues.Backpressure`) with `TaskQueueBackpressureMonitor`, `TaskQueueBackpressureSignal`, listener interfaces, limiter selector, and diagnostics listener implementations.
- XML docs and `docs/reference/hugo-api-reference.md` updates describing the APIs and configuration knobs.
- Sample wiring guide demonstrating OmniRelay dispatcher migration (codesnippet referencing RateLimiting middleware).
- Unit, integration, and feature tests (detailed below).

### Acceptance Criteria
1. Monitor accurately toggles state when queue pending count crosses thresholds and respects cooldown windows during churn (verified via automated tests).
2. Consuming middleware can plug `LimiterSelector` and observe throttling adjustments identical to OmniRelay’s current listener (prove via integration harness).
3. Diagnostics listener exposes latest signal + historical channel for HTTP/gRPC streaming endpoints without dropping events under load.
4. `GoDiagnostics` meters produce `hugo.taskqueue.backpressure.active`, `pending`, and `transitions` instruments with metadata ready for OmniRelay dashboards.
5. Migration spike demonstrates OmniRelay dispatcher can delete `ResourceLeaseBackpressure*` files in favor of the Hugo primitives without functionality loss (tracked in rollout notes, not necessarily merged).

### References
- `docs/reference/distributed-task-leasing.md:60-78` – current OmniRelay guidance for listener registration + metrics.
- `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:490-520` – manual gate + wait logic.
- `src/OmniRelay/Dispatcher/ResourceLeaseBackpressure.cs:4-26` – signal contract to mirror.
- `src/OmniRelay/Dispatcher/ResourceLeaseBackpressureListeners.cs:8-150` – rate limiter selector + diagnostics listener behaviour.

### Testing Strategy
- Combine deterministic unit tests (monitor math, cooldowns) with TaskQueue integration harness tests (simulate enqueue/lease operations) and feature tests that drive OmniRelay middleware against the library to validate end-to-end throttling.
- Stress-test channels and limiter hot swapping under concurrent producers/consumers to ensure no deadlocks or missed transitions.
- Automation should run under both `TaskQueue<T>` and `SafeTaskQueue<T>` to guarantee compatibility.

### Unit Test Scenarios
- Trigger transitions by enqueuing items past the high watermark and draining below the low watermark; assert state flips and timestamps.
- Validate cooldown enforcement by rapidly oscillating around thresholds; ensure redundant notifications are suppressed until cooldown elapses.
- Confirm `WaitForDrainingAsync` unblocks when `_isBackpressureActive` flips and cancels when the provided `CancellationToken` fires.
- Verify `TaskQueueBackpressureDiagnosticsListener` ring buffer capacity enforcement and snapshot updates.

### Integration Test Scenarios
- Spin up a `SafeTaskQueue<T>` with the monitor and run concurrent producers/consumers; assert limiter selector returns backpressure limiter only while pending count stays above threshold.
- Attach diagnostics listener to a Kestrel endpoint (similar to `ResourceLeaseBackpressureDiagnosticsListener` usage in `samples/ResourceLease.MeshDemo/Program.cs:63-137`) and stream events; verify ordering + delivery.
- Plug the monitor into OmniRelay’s `RateLimitingMiddleware` via the new selector and ensure RPC throughput throttles when queue is stressed, matching legacy listener behaviour.

### Feature Test Scenarios
- Deploy OmniRelay sample mesh with the Hugo monitor and collect metrics via `GoDiagnostics`; validate dashboards show the same fields as the bespoke instrumentation.
- Run chaos workload that spikes backlog, recovers, and spikes again; confirm auto-scaling automation can observe transitions via the diagnostics endpoint without gaps.
- Include canary toggles to switch between old/new monitors and compare latency/throughput/CPU to ensure no regressions.

## Story 2 – TaskQueue Replication Source & Deterministic Sink Helpers

### Goal
- Create a Hugo-level `TaskQueueReplicationSource` that emits ordered replication events, checkpointing sinks, and deterministic coordinators so OmniRelay (and other hosts) no longer need custom `ResourceLeaseReplicationEvent` plumbing (`src/OmniRelay/Dispatcher/ResourceLeaseReplication.cs:6-162`, `docs/reference/distributed-task-leasing.md:22-78`).

### Scope
- Covers replication for any `TaskQueue<T>`/`SafeTaskQueue<T>` mutation: enqueue, lease, heartbeat, ack, requeue, drain.
- Includes durable sink helpers (checkpointing, deterministic capture) but excludes persistence backends themselves (callers supply `IDeterministicStateStore`, blob, or DB writers).

### Requirements
1. Define a transport-neutral `TaskQueueReplicationEvent` record mirroring OmniRelay fields (sequence number, timestamp, peer/owner metadata, payload, error info) so migrations are mechanical.
2. Implement `TaskQueueReplicationSource` that subscribes to queue lifecycle hooks and emits ordered events via `IAsyncEnumerable<TaskQueueReplicationEvent>` or `ChannelWriter<TaskQueueReplicationEvent>`.
3. Provide `CheckpointingTaskQueueReplicationSink` base class replicating OmniRelay’s per-peer/global checkpoint tracking semantics to guarantee idempotent application even with retries (`src/OmniRelay/Dispatcher/ResourceLeaseReplication.cs:125-162`).
4. Ship deterministic helpers:
   - `TaskQueueDeterministicCoordinator` that wires events into `Hugo.DeterministicEffectStore` (see `docs/reference/deterministic-coordination.md:1-80`) and replays captured side effects.
   - JSON serialization context resolvers so hosts can persist events without hand-rolling contexts (`src/OmniRelay/Dispatcher/ResourceLeaseDeterministic.cs:9-133`).
5. Add hooks for replication lag metrics (sequence delta, wall-clock delta) and expose instrumentation consistent with `ResourceLeaseReplicationMetrics` in OmniRelay.

### Deliverables
- New Hugo package (e.g., `Hugo.TaskQueues.Replication`) containing the event contract, source, sinks, deterministic coordinator helpers, and serialization utilities.
- Documentation updates explaining replication wiring, deterministic replay, and peer checkpoint management (`docs/reference/hugo-api-reference.md` plus a dedicated reference page).
- Upgrade guidance for OmniRelay and other SafeTaskQueue adopters (sample code replacing `ResourceLeaseReplication*` types).
- Comprehensive automated tests and example integration harness.

### Acceptance Criteria
1. Replication events are strictly ordered, monotonic, and deduplicated within a single source even across concurrent queue operations.
2. Checkpointing sink correctly persists per-peer/global checkpoints and only forwards unseen events.
3. Deterministic coordinator captures payloads/events and can replay effects in failure/restart scenarios without duplicating work.
4. Metrics/tracing exported through `GoDiagnostics` align with OmniRelay’s current `omnirelay.resourcelease.replication.*` signals so dashboards stay intact.
5. Migration spike demonstrates OmniRelay can remove `ResourceLeaseReplication*` and `ResourceLeaseDeterministic*` files after adopting the Hugo package.

### References
- `src/OmniRelay/Dispatcher/ResourceLeaseReplication.cs:6-162` – current replication event contract + checkpointing logic.
- `src/OmniRelay/Dispatcher/ResourceLeaseDeterministic.cs:9-133` – deterministic coordinator + serializer context.
- `docs/reference/distributed-task-leasing.md:22-78` – replication stream rationale and operator guidance.
- `docs/reference/deterministic-coordination.md:1-80` – Hugo deterministic primitives to integrate.

### Testing Strategy
- Blend unit tests for event sequencing, serialization, and checkpoint math with integration tests that replay real queue workloads and feature tests that persist/replay through deterministic stores.
- Include chaos/resilience tests (simulated crashes) to validate idempotency and checkpoint recovery.

### Unit Test Scenarios
- Verify `TaskQueueReplicationEvent.Create` enforces incremental sequence numbers and populates timestamps with `TimeProvider`.
- Test checkpoint dictionary logic under concurrent event application; ensure per-peer checkpoints update independently.
- Confirm deterministic coordinator picks the correct `JsonSerializerContext` based on options (mirroring OmniRelay helper).

### Integration Test Scenarios
- Run a `TaskQueueReplicationSource` against an in-memory queue, enqueue + lease + ack items, and assert downstream sink receives ordered events even with parallel consumers.
- Simulate sink restarts with persisted checkpoints to ensure replay resumes from the correct sequence.
- Connect deterministic coordinator to `InMemoryDeterministicStateStore` (from tests/TestSupport) and validate recorded events can be replayed to rehydrate state.

### Feature Test Scenarios
- Deploy OmniRelay sample cluster using the Hugo replication source; verify peers subscribe to the stream over HTTP/gRPC and maintain consistent lease state.
- Drive failover scenario (kill active owner mid-lease) to confirm replication + deterministic capture provide enough data for audit trails and replays.
- Compare metrics emitted before/after migration to ensure `replication.events` counters and lag histograms remain accurate.

## Story 3 – TaskQueue Observability & Diagnostics Package

### Goal
- Bundle TaskQueue-specific metrics, tracing, and diagnostics under Hugo so operators can enable standardized instrumentation (meters, ActivitySources, sampling utilities) instead of maintaining OmniRelay-only code paths (`docs/reference/hugo-api-reference.md:85-166`, `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:520-638`).

### Scope
- Covers metrics/trace exporters, diagnostic listeners, and helper middleware for SafeTaskQueue-driven workflows (resource leasing, replication, throttling).
- Excludes vendor-specific exporters; the package should integrate with .NET `IMeterFactory`/`ActivitySource` registration patterns already documented in Hugo.

### Requirements
1. Define `Hugo.TaskQueues.Diagnostics` with:
   - `TaskQueueMetricsOptions` (enable/disable groups, configure histogram buckets, tag enrichers).
   - Default meters/counters for pending depth, active leases, throughput, replication lag, backpressure transitions (align with OmniRelay metrics in `docs/reference/distributed-task-leasing.md:60-78`).
2. Expose `ActivitySource` helpers + `UseRateLimitedSampling` shortcuts tailored for TaskQueue operations (building on `UseRateLimitedSampling` in `docs/reference/hugo-api-reference.md:160-163`).
3. Provide instrumentation middleware/adapters so `TaskQueueBackpressureMonitor` and `TaskQueueReplicationSource` automatically emit diagnostics when registered (zero-boilerplate).
4. Offer `TaskQueueDiagnosticsHost` sample that wires meters, activity sources, rate-limited sampling, and resets for unit tests (similar to OmniRelay’s `ResourceLeaseBackpressureDiagnosticsListener` control endpoint usage).
5. Document recommended dashboard queries + alerts referencing OmniRelay’s HTTP/gRPC control-plane endpoints.

### Deliverables
- New diagnostics namespace with extension methods (`AddTaskQueueDiagnostics`, `ConfigureTaskQueueMeters`, etc.).
- Updated reference docs plus runnable sample (could extend `samples/ResourceLease.MeshDemo` to consume the package).
- Instrumentation guidelines for OmniRelay maintainers describing migration steps.
- Associated unit/integration/feature tests.

### Acceptance Criteria
1. Calling `AddTaskQueueDiagnostics` registers meters/activity sources and returns disposable handles consistent with `GoDiagnostics` setup guidance.
2. Metrics cover pending depth, active leases, replication events, lag, and backpressure transitions with consistent tag sets (service, queue name, shard).
3. Activity sampling respects rate limits and integrates with .NET 10 sampling guidance (per `docs/reference/hugo-api-reference.md:160-163`).
4. Sample/control endpoint demonstrates streaming diagnostics (leveraging diagnostics listener) and can be consumed by CLI “watch” commands.
5. OmniRelay telemetry dashboards ingest the new instruments without schema changes (documented via rollout notes).

### References
- `docs/reference/hugo-api-reference.md:85-166` – existing Go diagnostics + sampling APIs to extend.
- `docs/reference/distributed-task-leasing.md:60-78` – metrics currently emitted for resource lease backpressure/replication.
- `samples/ResourceLease.MeshDemo/Program.cs:63-188` – control-plane endpoint exposing diagnostics stream today.
- `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:520-638` – replication publish path that logs metrics manually.

### Testing Strategy
- Validate instruments via unit tests that assert meter counters/histograms receive expected values after simulated queue operations.
- Integration tests should host a minimal worker, register diagnostics, and scrape metrics over `MeterListener` or OTLP exporters.
- Feature tests cover full OmniRelay sample mesh, verifying CLI/watch endpoints and Activity sampling toggles.

### Unit Test Scenarios
- Register diagnostics in isolation, simulate backpressure transitions, and confirm counters/histograms increment with correct tags.
- Ensure `UseRateLimitedSampling` returns `IDisposable` handle and toggles `ActivityListener` sampling rates as configured.
- Validate disposal/reset helpers clean up meters and listeners (matching `Diagnostics.Reset()` semantics in `docs/reference/hugo-api-reference.md:163`).

### Integration Test Scenarios
- Hook diagnostics into a hosted `TaskQueueBackpressureMonitor` + `TaskQueueReplicationSource`, run queue workload, and capture metrics via `MeterListener` to assert instrument names/values.
- Exercise Activity sampling with OpenTelemetry exporter to confirm spans respect max-per-interval quotas.
- Extend the mesh sample to expose `/diagnostics/taskqueue` endpoint and verify SSE/gRPC clients can stream metrics/backpressure events together.

### Feature Test Scenarios
- Deploy OmniRelay in a containerized environment using the diagnostics package, run hyperscale load (HTTP/gRPC), and compare dashboards before/after enabling the new metrics.
- Execute failure drills (queue jam, replication lag spike) and ensure alerts fire using the new instruments.
- Validate CLI `omnirelay control watch backpressure` (or equivalent) continues to work when pointing at Hugo-provided diagnostics endpoints.

