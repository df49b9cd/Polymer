# Hugo API Reference

This document enumerates every public type shipped with the Hugo library and its companion packages. It complements the focused guides in `docs/reference/` by providing a single place to explore the complete API surface, discover overloads, and understand how the building blocks relate to the .NET platform guidance.

- [Hugo namespace](#hugo-namespace)
  - [Error model](#error-model)
  - [Result primitives](#result-primitives)
  - [Optional values](#optional-values)
  - [Go concurrency toolkit](#go-concurrency-toolkit)
  - [Channel composition](#channel-composition)
  - [Task queue components](#task-queue-components)
  - [Diagnostics](#diagnostics)
  - [Deterministic orchestration](#deterministic-orchestration)
  - [Workflow telemetry](#workflow-telemetry)
- [Hugo.Policies namespace](#hugopolicies-namespace)
- [Hugo.Sagas namespace](#hugosagas-namespace)
- [Hugo.Diagnostics.OpenTelemetry namespace](#hugodiagnosticsopentelemetry-namespace)
- [Hugo.TaskQueues.Replication namespace](#hugotaskqueuesreplication-namespace)
- [Usage guidance and external references](#usage-guidance-and-external-references)

## Hugo namespace

### Error model

| Type | Description |
| --- | --- |
| `Error` | Structured error payload carrying a message, optional `Code`, an optional `Exception` cause, and case-insensitive metadata. Factory helpers cover `From`, `FromException`, `Canceled`, `Timeout`, `Unspecified`, and `Aggregate`. Use `WithMetadata` to attach contextual data (for example `WithMetadata("exceptionType", ex.GetType().FullName)`). `TryGetMetadata<T>` retrieves typed metadata. `Error` instances serialize to JSON via the built-in converter. |
| `ErrorCodes` | Centralised well-known codes emitted by Hugo (`error.validation`, `error.select.drained`, `error.taskqueue.lease_expired`, etc.). |

The `Error` type is decorated with the internal `ErrorJsonConverter`; when you `JsonSerialize` an `Error`, the payload (message, code, cause, metadata) is emitted without additional configuration.

### Result primitives

#### `Result<T>`

This readonly record struct encapsulates the success/failure outcome for an operation.

Key members:

- `IsSuccess` / `IsFailure` plus `Value`, `Error`, and `TryGetValue/TryGetError` helpers.
- `Switch` / `Match` and async variants `SwitchAsync` / `MatchAsync`.
- `ValueOr`, `ValueOr(Func<Error, T>)`, `ValueOrThrow()`, and `ToOptional()`.
- Implicit conversions to/from `(T Value, Error? Error)` maintain backward compatibility.
- `ResultException` is thrown by `ValueOrThrow()` and carries the originating `Error`.
- `CastFailure<TOut>()` reuses the existing error when you need to re-type a failure without incrementing diagnostics or allocating a new `Result`.

All factory methods ultimately increment success/failure counters through `GoDiagnostics`, so configure diagnostics before producing large volumes of results when you need metrics.

#### `Result` static helpers

`Result` is split across several partial classes:

- Creation: `Ok`, `Fail`, `FromOptional`, `Try`, `TryAsync`.
- Collection combinators: `Sequence`, `Traverse`, `Group`, `Partition`, and `Window` plus async counterparts for `IEnumerable` and `IAsyncEnumerable` sources. Cancellation tokens propagate through every async overload; failures short-circuit and surface the first `Error`.
- Streaming: `MapStreamAsync` creates an `IAsyncEnumerable<Result<T>>` that stops as soon as a failure or cancellation occurs.
- Batch execution: `WhenAll` returns the aggregated values when all operations succeed; `WhenAny` resolves as soon as the first success arrives and compensates remaining operations.
- Retry orchestration: `RetryWithPolicyAsync` executes a delegate under a `ResultExecutionPolicy` (see [Policies](#hugopolicies-namespace)).
- Fallbacks: `TieredFallbackAsync` evaluates ordered `ResultFallbackTier<T>` groups, racing strategies within a tier and enriching errors with `fallbackTier`, `tierIndex`, and `strategyIndex` metadata.
- Streaming helpers (from `Result.Streaming`): `MapStreamAsync`, `FlatMapStreamAsync`, `FilterStreamAsync`, `ToChannelAsync`, `ReadAllAsync`, `FanInAsync`, `FanOutAsync`, `WindowAsync`, `PartitionAsync`, plus consumption helpers (`ForEachAsync`, `ForEachLinkedCancellationAsync`, `TapSuccessEachAsync`, `TapFailureEachAsync`, `CollectErrorsAsync`).
- Streaming tap variants: `TapSuccessEachAggregateErrorsAsync` / `TapFailureEachAggregateErrorsAsync` (invoke taps, traverse the whole stream, and surface an aggregate error if any failures occur) and `TapSuccessEachIgnoreErrorsAsync` / `TapFailureEachIgnoreErrorsAsync` (invoke taps but always return success).

#### `Functional` extensions

`Functional` is a static class housing the “railway oriented” fluent API. Extensions exist in synchronous and asynchronous forms and return `Result<T>` (or tasks of results) so they compose naturally.

- Execution flow: `Then`, `ThenAsync` overloads (sync→sync, sync→async, async→sync, async→async) plus `ThenValueTaskAsync` for ValueTask-based sources, along with `Recover`, `RecoverAsync`, `RecoverValueTaskAsync` and `Finally`, `FinallyAsync`, `FinallyValueTaskAsync`. All async helpers accept `ValueTask<Result<T>>` delegates without forcing extra `Task` allocations.
- Mapping: `Map`, `MapAsync` (sync source/async mapper), `MapValueTaskAsync`, `Tap`, `TapAsync`, `TapValueTaskAsync`, and aliases `Tee`, `TeeAsync`, `TeeValueTaskAsync`.
- Validation: `Ensure`, `EnsureAsync`, `EnsureValueTaskAsync`, LINQ integration (`Select`, `SelectMany`, `Where`).
- Side-effects: `OnSuccess`/`OnSuccessAsync`/`OnSuccessValueTaskAsync`, `OnFailure`/`OnFailureAsync`/`OnFailureValueTaskAsync`, `TapError`/`TapErrorAsync`/`TapErrorValueTaskAsync`.

Cancellations are normalised using `Error.Canceled`, preserving the triggering token in metadata where possible.

### Optional values

`Optional<T>` is a small readonly record struct that models the presence or absence of a value.

- Use `Some(value)` (throws on `null`) or `Optional<T>.None()`.
- Inspect with `HasValue/HasNoValue`, `Value`, `TryGetValue`, `Match`, `Switch`.
- Transform with `Map`, `Bind`, `Filter`, `Or`.
- Convert to `Result<T>` via `ToResult(Func<Error>)`.

The companion static class `Optional` offers convenience factories (`FromNullable`) mirroring value/reference type handling.

### Go concurrency toolkit

#### Core helpers (`Go` static class)

`Go` provides Go-style primitives layered on top of .NET 10:

- Timers: `DelayAsync`, `NewTicker`, `Tick`, `After`, `AfterAsync`, `AfterValueTaskAsync`. All overloads accept an optional `TimeProvider`; `TimeProvider.System` is used by default so the APIs align with `Task.Delay(TimeSpan, TimeProvider)` guidance from [.NET TimeProvider docs](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview). The returned `GoTicker` implements `IDisposable`/`IAsyncDisposable`.
- Select workflows: `SelectAsync` (with/without timeout) and `Select<TResult>(...)` builder creation. Internally the logic honours case priority and speculative reads for ready channels. Timeouts rely on the supplied `TimeProvider` — use fake providers in deterministic tests.
- Channel builders: `BoundedChannel<T>(capacity)`, `PrioritizedChannel<T>()`, `PrioritizedChannel<T>(levels)` return fluent builders (see [Channel composition](#channel-composition)).
- Channel fan-in/out: `SelectFanInAsync` (task-first overloads), `SelectFanInValueTaskAsync` (ValueTask-friendly overloads), `FanInAsync`/`FanIn`, `FanOutAsync`/`FanOut`. Each method propagates `Error.Canceled`, ensures writers are completed appropriately while completing writers/readers deterministically, and interprets any supplied timeout as an overall session deadline measured once with the active `TimeProvider`.
- Pipeline orchestration: `FanOutAsync` / `FanOutValueTaskAsync` materialise concurrent `Result<T>` operations via `Result.WhenAll`, `RaceAsync` / `RaceValueTaskAsync` surface the first success through `Result.WhenAny`, `WithTimeoutAsync` / `WithTimeoutValueTaskAsync` wrap work with deadline-aware cancellation (returning `Error.Timeout` on deadline expiry and `Error.Canceled` when the caller’s token is triggered), and `RetryAsync` / `RetryValueTaskAsync` execute delegates under an exponential backoff policy built from `ResultExecutionPolicy` (cancellations — even from linked tokens — short-circuit retries and surface `Error.Canceled`).
- Task runners: `Run(Func<Task>)` / `Run(Func<CancellationToken, Task>)` expose optional `TaskScheduler` + `TaskCreationOptions` parameters so you can specify long-running or inline schedulers, while `Run(Task)`, `Run(Task<T>)`, and the `ValueTask` overloads allow callers to register already-started work without additional `Task.Run` allocations.
- Channel factories: `MakeChannel` overloads accept optional capacity/`BoundedChannelOptions`/`UnboundedChannelOptions` or `PrioritizedChannelOptions`. `MakePrioritizedChannel` creates multi-level queues with default priority support.
- Result helpers: `Ok<T>` and `Err<T>` wrap `Result.Ok`/`Result.Fail`. `CancellationError` exposes `Error.Canceled()` for convenience.

Additional types:

- `Go.Unit`: value-type sentinel (`Unit.Value`).
- `Go.GoTicker`: wrapper over `TimerChannel`; exposes `Reader`, `ReadAsync`, `TryRead`, `Stop`, `StopAsync`.
- `Defer`: RAII helper mirroring Go’s `defer`, executing the supplied `Action` when disposed.
- `GoWaitGroupExtensions.Go`: extension overloads for `WaitGroup` (async and cancellation-aware) that forward optional `TaskScheduler`/`TaskCreationOptions` hints into the core `WaitGroup.Go` pipeline.

#### Synchronisation primitives

| Type | Highlights |
| --- | --- |
| `WaitGroup` | Tracks outstanding async operations. `Add(int)`, `Add(Task)`, `Go(Func<Task>, CancellationToken = default, TaskScheduler? scheduler = null, TaskCreationOptions creationOptions = TaskCreationOptions.DenyChildAttach)`, `Go(Task)`, `Go(ValueTask)`, `Done()`, `WaitAsync(CancellationToken)` plus `WaitAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default)` (returns `bool`). Diagnostics emit `waitgroup.*` metrics. Avoid negative counters — the implementation guards against it. |
| `Mutex` | Hybrid mutex with synchronous `EnterScope()` (returns a disposable scope) and asynchronous `LockAsync`. Always dispose the returned scope/releaser. |
| `RwMutex` | Reader/writer lock with sync (`EnterReadScope`, `EnterWriteScope`) and async (`RLockAsync`, `LockAsync`) APIs. Cancelled acquisitions propagate `OperationCanceledException`. |
| `Once` | Executes an `Action` at most once. Subsequent calls no-op. |
| `Pool<T>` | Concurrent bag with optional `New` factory. `Get()` obtains existing or new instances; `Put(T)` returns them. |
| `ErrGroup` | Coordinates multiple tasks, cancelling peers on first failure. Overloads accept delegates returning `Result<Unit>` or plain tasks/actions. Properties `Token`, `Error`. `Go(...)` overload that accepts a `ResultExecutionPolicy` slot integrates retry/compensation logic. `WaitAsync` surfaces the first error as a `Result<Unit>`. |

These primitives follow the .NET parallel programming recommendations — always honour the cancellation token passed to async methods to avoid blocking `TestContext.Current.CancellationToken`.

### Channel composition

#### Channel cases and select builder

- `ChannelCase.Create<T, TResult>` overloads materialise cases around `ChannelReader<T>` plus an action returning `ValueTask<Result<TResult>>`. Variants exist for ValueTask delegates (`Func<T, CancellationToken, ValueTask<Result<TResult>>>`), simplified forms without the cancellation token, plain `ValueTask` callbacks, and synchronous actions. `ChannelCase.CreateDefault<TResult>` generates default cases (optionally with priority). Use `WithPriority(int)` to override the selection priority (lower number == higher priority).
- `ChannelCaseTemplate<T>` wraps a reader and exposes `.With(...)` helpers so you can pre-build case templates and materialise them later.
- `ChannelCaseTemplates.With<T, TResult>(...)` materialises a batch of templates by projecting each into a `ChannelCase<TResult>`.
- `SelectBuilder<TResult>` orchestrates strongly typed select flows. Call `.Case(...)` using the overload matching your continuation, optionally specifying `priority`. `.Default(...)` and `.Deadline(TimeSpan dueIn, ...)` register default branches or timer-based cases (using `Go.After`). Finally, call `ExecuteAsync()` to obtain `Result<TResult>`. The builder reuses `Go.SelectInternalAsync` so metrics and cancellation semantics are identical.

#### Channel builders and DI helpers

| Type | Description |
| --- | --- |
| `BoundedChannelBuilder<T>` | Fluent API over `BoundedChannelOptions`. Configure `WithCapacity`, `WithFullMode`, `SingleReader`, `SingleWriter`, `AllowSynchronousContinuations`, and arbitrary `Configure` callbacks. Call `Build()` to produce a `Channel<T>`. |
| `PrioritizedChannelBuilder<T>` | Fluent API for `PrioritizedChannelOptions`; configure `WithPriorityLevels`, `WithDefaultPriority`, `WithCapacityPerLevel`, `WithPrefetchPerPriority`, `WithFullMode`, `SingleReader`, `SingleWriter`, and `Configure`. `Build()` returns a `PrioritizedChannel<T>`. Set `SingleReader()` when only one consumer drains the channel to enable the lean per-lane buffer. |
| `ChannelServiceCollectionExtensions` | Dependency injection helpers: `AddBoundedChannel<T>` and `AddPrioritizedChannel<T>` register the channel plus the associated reader/writer (and prioritized reader/writer) as services with a configurable `ServiceLifetime`. |
| `PrioritizedChannel<T>` | Combines multiple channel lanes. Exposes `Reader`/`Writer`, plus `PrioritizedReader` (merges items according to priority) and `PrioritizedWriter` (explicit `WriteAsync`, `TryWrite`, `WaitToWriteAsync` per priority level). `PriorityLevels`, `DefaultPriority`, `CapacityPerLevel`, and `PrefetchPerPriority` expose configuration. |

Hugo follows the `.NET Channels` guidance published at [learn.microsoft.com/dotnet/core/extensions/channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels): bounded channels apply `BoundedChannelFullMode`, unbounded channels allow synchronous continuations when requested, and `WaitToReadAsync` loops guard against race conditions.

### Task queue components

| Type | Description |
| --- | --- |
| `TaskQueueOptions` | Immutable configuration: `Name`, `Capacity`, `LeaseDuration`, `HeartbeatInterval`, `LeaseSweepInterval`, `RequeueDelay`, `MaxDeliveryAttempts`, plus optional `Backpressure` callbacks (used directly or by `TaskQueueBackpressureMonitor`). Validation enforces positive/ non-negative ranges. |
| `TaskQueue<T>` | Channel-backed cooperative lease queue. Key members: `EnqueueAsync`, `LeaseAsync`, `PendingCount`, `ActiveLeaseCount`, `DrainPendingItemsAsync`, `RestorePendingItemsAsync`, `ConfigureBackpressure`, `QueueName`, and `DisposeAsync`. Internally tracks leases, heartbeats, requeues, and dead-letter routing while emitting `taskqueue.*` metrics/activities. |
| `TaskQueueLease<T>` | Represents an active lease. Use `Value`, `Attempt`, `EnqueuedAt`, `LastError`, `SequenceId`, and `OwnershipToken`. Methods: `CompleteAsync`, `HeartbeatAsync`, `FailAsync(Error error, bool requeue = true)`. Multiple invocations throw when the lease is no longer active. |
| `SafeTaskQueueWrapper<T>` | Result-friendly adapter over `TaskQueue<T>`. Methods `EnqueueAsync`, `LeaseAsync`, `Wrap`, `DisposeAsync`. Converts cancellation, disposal, and invalid operations into structured `Error` codes (for example `error.taskqueue.disposed`). Optional `ownsQueue` flag disposes the underlying queue on teardown. |
| `SafeTaskQueueLease<T>` | Lightweight wrapper around `TaskQueueLease<T>` that returns `Result<Unit>` from `CompleteAsync`, `HeartbeatAsync`, and `FailAsync`. Normalises inactive leases to `error.taskqueue.lease_inactive`, exposes `SequenceId` / `OwnershipToken`, and captures cancellation metadata. |
| `TaskQueueDeadLetterContext<T>` | Payload delivered to dead-letter handlers when the queue cannot retry an item. Includes `Value`, `Error`, `Attempt`, `EnqueuedAt`, `SequenceId`, and the last `OwnershipToken`. |
| `TaskQueueOwnershipToken` | Monotonic lease identifier composed of `SequenceId`, `Attempt`, and the active `LeaseId`. Useful as a fencing token for metadata stores. |
| `TaskQueuePendingItem<T>` | Serializable snapshot captured by `DrainPendingItemsAsync`. Contains the payload, attempt, timestamps, last error, and ownership token so work can be restored losslessly. |
| `TaskQueueLifecycleEvent<T>` | Immutable payload describing queue mutations observed by replication sources. Includes event and queue sequence numbers, timestamps, payload, errors, ownership tokens, and lifecycle flags. |
| `ITaskQueueLifecycleListener<T>` | Observer interface invoked synchronously for every lifecycle event. Used by replication sources and diagnostics to subscribe to queue mutations without wrapping the queue. |
| `TaskQueueBackpressureOptions` | Optional configuration attached to `TaskQueueOptions` or applied via `TaskQueue.ConfigureBackpressure`. Configure `HighWatermark`, `LowWatermark`, `Cooldown`, and `StateChanged(TaskQueueBackpressureState state)` to drive throttling decisions. |
| `TaskQueueBackpressureState` | Immutable payload delivered to backpressure callbacks containing `IsActive`, `PendingCount`, and `ObservedAt`. |
| `TaskQueueBackpressureMonitorOptions` | Configures `TaskQueueBackpressureMonitor<T>` instances (high/low watermarks and cooldown). |
| `TaskQueueBackpressureMonitor<T>` | Wraps `TaskQueue<T>` or `SafeTaskQueueWrapper<T>`, configures queue backpressure thresholds, emits `TaskQueueBackpressureSignal` values, exposes `CurrentSignal`, `IsActive`, `RegisterListener`, and `WaitForDrainingAsync`, and records `hugo.taskqueue.backpressure.*` metrics. |
| `TaskQueueBackpressureSignal` | Strongly typed event containing `IsActive`, `PendingCount`, `HighWatermark`, `LowWatermark`, and `ObservedAt`. |
| `ITaskQueueBackpressureListener` | Interface for components that react to monitor signals via `ValueTask OnSignalAsync(TaskQueueBackpressureSignal signal, CancellationToken token)`. |
| `BackpressureAwareRateLimiter` / `TaskQueueLimiterSelector` | Default listener that swaps between two `RateLimiter` instances whenever backpressure toggles. `LimiterSelector` plugs directly into `RateLimitingMiddleware`. |
| `TaskQueueBackpressureDiagnosticsListener` | Listener that buffers recent signals and exposes a `ChannelReader<TaskQueueBackpressureSignal>` plus `Latest` snapshot for HTTP/gRPC streaming endpoints. |
| `TaskQueueHealthCheck<T>` / `TaskQueueHealthCheckOptions` | Health check implementation for queues. Register via `services.AddTaskQueueHealthCheck<T>(...)` to expose `/health` probes keyed off pending/active thresholds. |
| `TaskQueueChannelAdapter<T>` | Bridges `TaskQueue<T>` into a `Channel<TaskQueueLease<T>>`. Static `Create` accepts the queue, optional channel, concurrency (number of background lease pumps), and ownership flag; when no channel is supplied the adapter builds a bounded channel sized to `concurrency` so at most that many leases sit in-flight. Properties `Reader`, `Queue`; `DisposeAsync` stops pumps and optionally disposes the queue. |

Task queue internals honour cancellation tokens, propagate `Error.Canceled`, and surface lease expirations via `error.taskqueue.lease_expired` with metadata for `attempt`, `enqueuedAt`, and `expiredAt`.

### Diagnostics

`GoDiagnostics` centralises metric and tracing instrumentation.

- Meter registration: `Configure(IMeterFactory factory, string? meterName = null)` or `Configure(Meter meter)`.
- Activity registration: `CreateActivitySource(name?, version?, schemaUrl?)`, `Configure(ActivitySource source)`.
- Combined setup: `Configure(Meter meter, ActivitySource activitySource)`.
- Rate limiting: `UseRateLimitedSampling(ActivitySource source, int maxActivitiesPerInterval, TimeSpan interval, ActivitySamplingResult unsampledResult = PropagationData)` returns an `IDisposable` that installs an `ActivityListener` throttling span creation. This aligns with the .NET 10 behavioral change described in [Activity sampling guidance](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/activity-sampling).
- Reset for tests: `Reset()` disposes all registered meters/sources and clears instrument references.
- TaskQueue instrumentation: `ConfigureTaskQueueMetrics(TaskQueueMetricGroups groups)` toggles the metric families emitted by `TaskQueue<T>`/`SafeTaskQueueWrapper<T>`, while `RegisterTaskQueueTagEnricher(TaskQueueTagEnricher enricher)` appends additional tags (`service.name`, shard identifiers, tenant metadata, etc.) to each measurement.
- Tag enrichers receive a `TaskQueueTagContext` (exposes the queue name) and mutate a `TagList` via the `TaskQueueTagEnricher` delegate.
- `TaskQueueMetricsOptions`, `TaskQueueActivityOptions`, and `TaskQueueDiagnosticsOptions` collect the knobs exposed by the `Hugo.TaskQueues.Diagnostics` package. Call `IMeterFactory.AddTaskQueueDiagnostics(...)` (or the `IServiceProvider` overload) to configure meters, activity sources, rate-limited sampling, metric groups, and tag enrichers in one statement. The helper returns a `TaskQueueDiagnosticsRegistration` (an `IAsyncDisposable`) that tears down the instrumentation when disposed.
- `TaskQueueDiagnosticsHost` aggregates `TaskQueueBackpressureMonitor<T>` and `TaskQueueReplicationSource<T>` signals. Use `Attach` overloads to register monitors/sources, then stream the `ChannelReader<TaskQueueDiagnosticsEvent>` output to HTTP/gRPC clients. `TaskQueueBackpressureDiagnosticsEvent` and `TaskQueueReplicationDiagnosticsEvent` subclasses carry the structured payloads.

When configured, select operations record attempts, completions, timeouts, cancellations, latency, and channel depth; wait groups and task queues also increment their respective counters.

### Deterministic orchestration

| Type | Description |
| --- | --- |
| `IDeterministicStateStore` | Abstraction over durable storage for deterministic records. Methods: `TryGet(string key, out DeterministicRecord)` and `Set(string key, DeterministicRecord record)`. |
| `DeterministicRecord` | Immutable container storing `Kind`, `Version`, `RecordedAt`, and payload (`ReadOnlyMemory<byte>`). |
| `InMemoryDeterministicStateStore` | Concurrent dictionary-backed store suitable for testing. |
| `DeterministicEffectStore` | Executes side-effects once and replays stored `Result<T>` values. `CaptureAsync` overloads accept async sync delegates; stored payloads validate type names and rehydrate successes or errors. |
| `VersionGate` | Records or retrieves version markers using optimistic inserts (`IDeterministicStateStore.TryAdd`). `Require` accepts `changeId`, `[minVersion, maxVersion]`, and optional initial version provider. Returns `Result<VersionDecision>` (`IsNew`, `RecordedAt`). Concurrent writers that lose the CAS receive `error.version.conflict`. |
| `VersionDecision` / `VersionGateContext` | Data carriers consumed by higher-level orchestration. |
| `DeterministicGate` | Coordinates version decisions with effect capture. `ExecuteAsync` overloads choose between `legacy` and `upgraded` flows (or custom `Func<VersionDecision, CancellationToken, Task<Result<T>>>`). The nested `DeterministicWorkflowBuilder<TResult>` supports version-specific branches plus fallback logic. |
| `DeterministicGate.DeterministicWorkflowContext` | Provides per-step helpers, including `CaptureAsync` to persist deterministic effects with scoped IDs (`changeId::v{version}::{stepId}`). |

These components integrate with `WorkflowExecutionContext` and instrumentation so deterministic workflows emit consistent metadata across replays.

### Workflow telemetry

| Type | Description |
| --- | --- |
| `WorkflowExecutionContext` | Carries namespace, identifiers, metadata, `TimeProvider`, and logical clock state. Use `MarkStarted()` to emit metrics/spans, `SnapshotVisibility()` for current state, `Complete` or `TryComplete` to finalise with `WorkflowCompletionStatus`. Methods `Tick`, `Observe`, `ResetLogicalClock`, `IncrementReplayCount`, and metadata accessors support deterministic coordination. `AttachError` enriches an `Error` with workflow metadata. |
| `WorkflowVisibilityRecord` | Immutable snapshot describing workflow visibility information (namespace, IDs, status, timestamps, logical clock, replay count, additional attributes). |
| `WorkflowStatus` | High-level visibility states (`Active`, `Completed`, `Failed`, `Canceled`, `Terminated`). |
| `WorkflowCompletionStatus` | Completion outcomes recorded on the execution (`Completed`, `Failed`, `Canceled`, `Terminated`). |

`WorkflowExecutionContext` interacts with `GoDiagnostics` to increment `workflow.*` metrics and emit OpenTelemetry activities when configured.

## Hugo.Policies namespace

| Type | Description |
| --- | --- |
| `ResultExecutionPolicy` | Immutable record encapsulating `ResultRetryPolicy` and `ResultCompensationPolicy`. `None` is the default; `WithRetry`/`WithCompensation` produce copies with the desired settings. `EffectiveRetry` and `EffectiveCompensation` always return a non-null policy. |
| `ResultRetryPolicy` | Defines retry strategy: `None`, `FixedDelay`, `Exponential`, and `Cron`. Each policy builds a `RetryState` using the supplied `TimeProvider`. `EvaluateAsync` registers the failure and returns a `RetryDecision`. |
| `ResultCompensationPolicy` | Wraps the execution strategy for compensation actions. `None`, `SequentialReverse`, and `Parallel` are provided. |
| `RetryDecision` | Value type describing whether to retry, optionally after a delay (`RetryAfter`) or at a scheduled timestamp (`RetryAt`). |
| `RetryState` | Tracks attempts, delays, multiplier, maximum delay, last error, timestamps, and accumulated error list. Exposes `SetActivityId`, `RegisterFailure`, and `Reset`. |
| `CompensationAction` | Lightweight record linking a callback and payload captured when registering compensations. |
| `CompensationScope` | Collects actions emitted during pipeline execution. `Register`, `Capture`, `Absorb`, `Clear`, and `HasActions` orchestrate compensation lifecycles. |
| `CompensationContext` | Provides execution semantics for compensation (`ExecuteAsync` sequentially or `parallel: true`). |
| `ResultPipelineStepContext` | Passed to pipeline steps executed under a policy. Exposes `StepName`, `TimeProvider`, `CancellationToken`, and `RegisterCompensation` (with optional state + conditional registration). |
| `ResultPipelineChannels` | Pipeline-aware wrappers over Go channel helpers (`SelectAsync`, `FanInAsync`, `MergeAsync`, `BroadcastAsync`) that automatically absorb compensation scopes emitted by continuations. |
| `ResultPipeline` | High-level orchestration wrappers that mirror Go helpers (`FanOutAsync`, `RaceAsync`, `RetryAsync`, `WithTimeoutAsync`) while returning `ValueTask<Result<T>>` and preserving pipeline diagnostics. |
| `ResultPipelineSelectBuilder<TResult>` | Fluent select builder that threads `ResultPipelineStepContext` through every channel case so compensations and cancellations remain aligned with the active pipeline scope. |
| `ResultPipelineWaitGroupExtensions` | Adds pipeline-aware `Go(...)` helpers for `WaitGroup`, ensuring spawned work inherits the parent cancellation token and registers compensations automatically. |
| `ResultPipelineErrGroupExtensions` | Bridges `ErrGroup` with `ResultPipelineStepContext` so errgroup steps share the parent pipeline’s compensation scope. |
| `ResultPipelineTimers` | Delay/after/ticker helpers that reuse the pipeline `TimeProvider`, link cancellation tokens, and register compensations to dispose timers deterministically. |
| `ResultExecutionBuilders` | Convenience helpers: `FixedRetryPolicy`, `ExponentialRetryPolicy`, and `CreateSaga` (which seeds a `ResultSagaBuilder` with the supplied configuration delegates). |

Policies rely on `TimeProvider` for scheduling; when testing, inject `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` to deterministically advance timers.

## Hugo.Sagas namespace

| Type | Description |
| --- | --- |
| `ResultSagaBuilder` | Fluent saga composition. `AddStep` registers named steps returning `Result<TState>` and optional compensation callbacks; results are stored in `ResultSagaState` under the provided key. `ExecuteAsync` runs the saga under a `ResultExecutionPolicy` and returns the populated state or failure. |
| `ResultSagaState` | Mutable dictionary of step outputs (`Set`/`TryGet`). Keys are case-insensitive. |
| `ResultSagaStepContext` | Wraps `ResultPipelineStepContext` for sagas. Exposes `StepName`, `TimeProvider`, `State`, and `RegisterCompensation` helpers (including `TryRegisterCompensation`). |

Saga execution respects cancellation tokens and leverages compensation scopes from the configured policy.

## Hugo.Diagnostics.OpenTelemetry namespace

| Type | Description |
| --- | --- |
| `HugoOpenTelemetryBuilderExtensions` | Adds Hugo diagnostics to an `OpenTelemetryBuilder` or `IHostApplicationBuilder`. `AddHugoDiagnostics(Action<HugoOpenTelemetryOptions>?)` configures resources, meters, trace sources, OTLP/Prometheus exporters, runtime instrumentation, and the hosted service that registers `GoDiagnostics`. The host builder overload mirrors ASP.NET Core / .NET Aspire defaults. |
| `HugoOpenTelemetryOptions` | Options object consumed by the extension. Properties cover schema (`SchemaUrl`), service identity (`ServiceName`), instrument names (`MeterName`, `ActivitySourceName`, `ActivitySourceVersion`), toggles for meter/activity configuration, rate-limited sampling (`EnableRateLimitedSampling`, `MaxActivitiesPerInterval`, `SamplingInterval`, `UnsampledActivityResult`), exporter settings (`AddOtlpExporter`, `OtlpProtocol`, `OtlpEndpoint`, `AddPrometheusExporter`), and runtime metrics (`AddRuntimeInstrumentation`). |
| `HugoDiagnosticsRegistrationService` | Internal hosted service that wires `GoDiagnostics` into OpenTelemetry when the extensions are used. Automatically applies rate-limited sampling to workflows to avoid excessive spans, aligning with the [OpenTelemetry .NET best practices](https://opentelemetry.io/docs/languages/dotnet/traces/best-practices/). |

## Hugo.TaskQueues.Replication namespace

| Type | Description |
| --- | --- |
| `TaskQueueReplicationSource<T>` | Observes `TaskQueue<T>` mutations via lifecycle listeners and emits `TaskQueueReplicationEvent<T>` instances through `IAsyncEnumerable`/`ChannelReader`. Records `taskqueue.replication.events` and lag histograms through `GoDiagnostics`. `RegisterObserver(ITaskQueueReplicationObserver<T>)` mirrors the event stream to diagnostics hosts or custom sinks without draining the primary channel. |
| `ITaskQueueReplicationObserver<T>` | Observer interface invoked for every replication event (`ValueTask OnReplicationEventAsync(TaskQueueReplicationEvent<T> evt, CancellationToken token)`). Used by `TaskQueueDiagnosticsHost` to tap the stream without consuming the primary reader. |
| `TaskQueueReplicationSourceOptions<T>` | Configures a replication source (`SourcePeerId`, `DefaultOwnerPeerId`, `OwnerPeerResolver`, `TimeProvider`). |
| `TaskQueueReplicationEvent<T>` / `TaskQueueReplicationEventKind` | Transport-neutral event contract covering queue name, replication sequence, lifecycle kind, payload, ownership tokens, timestamps, and replication flags. The static `Create` helper enforces monotonic sequences and stamps timestamps with the configured `TimeProvider`. |
| `CheckpointingTaskQueueReplicationSink<T>` | Base class that consumes replication streams with per-peer/global checkpoints backed by an `ITaskQueueReplicationCheckpointStore`. Override `ApplyEventAsync` to forward events to HTTP writers, gRPC relays, etc. |
| `ITaskQueueReplicationCheckpointStore` | Abstraction for durable checkpoint persistence (SQL, Cosmos DB, blob, deterministic state). Methods read/persist `TaskQueueReplicationCheckpoint` snapshots. |
| `TaskQueueReplicationCheckpoint` | Immutable checkpoint structure containing the stream id, global event position, last update time, and per-peer offsets. Helpers evaluate whether events should be processed and advance peer/global state safely. |
| `TaskQueueDeterministicCoordinator<T>` | Bridges replication events with `DeterministicEffectStore`. `ExecuteAsync` captures a handler result under the event’s effect id so replays reuse the original outcome without re-running side effects. |
| `TaskQueueReplicationJsonSerialization` / `TaskQueueReplicationJsonContext<T>` | Source-generated `JsonSerializerContext` helpers used when persisting replication events or configuring deterministic stores for replay. |

Refer to `docs/reference/taskqueue-replication.md` for wiring guidance, checkpoint examples, and deterministic replay walkthroughs.

## Usage guidance and external references

- **System.Threading.Channels best practices**: consult [Channels - .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels) for bounding strategies, full-mode behaviour, and producer/consumer patterns. Hugo’s channel builders surface the same options (`BoundedChannelFullMode.DropNewest`, `DropOldest`, etc.).
- **Time abstractions**: rely on `TimeProvider` for delays and timers. Tests should use `FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package (see [TimeProvider overview](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview)). All timeout-aware APIs in Hugo accept an optional `TimeProvider` to facilitate deterministic execution.
- **Activity sampling changes in .NET 10**: when using `GoDiagnostics.CreateActivitySource`, be mindful of the updated sampling semantics described in [ActivitySource sampling behavioural change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/activity-sampling). The built-in rate-limited sampler helps keep spans under control.
- **OpenTelemetry guidance**: follow the [OpenTelemetry .NET best practices](https://opentelemetry.io/docs/languages/dotnet/traces/best-practices/) for resource naming, `ActivitySource` reuse, and avoiding excessive manual activity creation. `HugoOpenTelemetryBuilderExtensions` already registers the recommended single `TracerProvider`/`MeterProvider` per process.
- **Cancellation**: Hugo surfaces cancellation consistently as `Error.Canceled` (with the triggering token in metadata when available) and `OperationCanceledException`. Honour the incoming token on all async calls to integrate cleanly with `TestContext.Current.CancellationToken`.
- **Testing deterministically**: combine `DeterministicEffectStore`, `VersionGate`, and `WorkflowExecutionContext` with fake time providers to rehydrate the same logical clock and recorded effects during replay.

This reference is intentionally exhaustive; the domain guides (`result-pipelines.md`, `concurrency-primitives.md`, `deterministic-coordination.md`, `diagnostics.md`) remain the best starting point when you need a task-focused walkthrough. Use this document when you need to audit overloads, discover less obvious helpers, or verify instrumentation capabilities.
