# Concurrency Primitives Reference

This reference describes the concurrency types exposed through `Hugo.Go`. Each section lists primary APIs, behaviour, and diagnostics emitted via `GoDiagnostics`.

## Quick navigation

- [WaitGroup](#waitgroup)
- [Mutex](#mutex)
- [RwMutex](#rwmutex)
- [Channels](#channels)
- [Task queue leasing](#task-queue-leasing)
- [Select / fan-in utilities](#fan-in-utilities)
- [Result orchestration helpers](#result-orchestration-helpers)
- [Timers](#timers)
- [Deterministic utilities](#deterministic-utilities)

> **Best practice:** Configure `GoDiagnostics` (or call `AddHugoDiagnostics`) before constructing these primitives so counters, histograms, and activity sources capture the full lifecycle.

## WaitGroup

Tracks asynchronous operations and delays shutdown until every task completes.

### Key members

- `WaitGroup.Add(int delta)` / `Add(Task task)`
- `WaitGroup.Go(Func<Task> work, CancellationToken cancellationToken = default, TaskScheduler? scheduler = null, TaskCreationOptions creationOptions = TaskCreationOptions.DenyChildAttach)`
- `WaitGroup.Go(Task task)` / `WaitGroup.Go(ValueTask task)`
- `WaitGroup.Done()`
- `WaitGroup.WaitAsync(CancellationToken cancellationToken = default)`
- `WaitGroup.WaitAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default)` (returns `bool`)
- `GoWaitGroupExtensions.Go(Func<CancellationToken, Task> work, CancellationToken cancellationToken, TaskScheduler? scheduler = null, TaskCreationOptions creationOptions = TaskCreationOptions.DenyChildAttach)` when you prefer to pass an explicit token along with scheduling hints

### WaitGroup usage

```csharp
var wg = new WaitGroup();

wg.Go(async () =>
{
    await Task.Delay(50, cancellationToken);
});

var completed = await wg.WaitAsync(
    timeout: TimeSpan.FromMilliseconds(250),
    provider: timeProvider,
    cancellationToken: cancellationToken);
```

### WaitGroup notes

- `WaitAsync(TimeSpan, ...)` returns `false` when a timeout elapses; the parameterless overload completes when the counter reaches zero.
- Cancellation surfaces as `Error.Canceled` in result pipelines and `OperationCanceledException` otherwise.
- Diagnostics: `waitgroup.additions`, `waitgroup.completions`, `waitgroup.outstanding`.
- Prefer the scheduler-aware `Go` overloads when you need `TaskCreationOptions.LongRunning` or a custom `TaskScheduler`, and use `Go(Task)` / `Go(ValueTask)` (or `Go.Run(ValueTask)` + `WaitGroup.Add`) when you already have a running operation that should be tracked without an extra `Task.Run`.

## Mutex

Provides mutual exclusion with synchronous (`EnterScope`) and asynchronous (`LockAsync`) releasers.

### Mutex usage

```csharp
var mutex = new Mutex();

await using (await mutex.LockAsync(cancellationToken))
{
    // Exclusive access
}
```

### Mutex notes

- Pending waiters honour cancellation tokens.
- Dispose the releaser (via `await using`/`using`) to release the lock.

## RwMutex

Reader/writer lock that allows concurrent readers or a single writer.

### RwMutex usage

```csharp
var rwMutex = new RwMutex();

await using (await rwMutex.RLockAsync(ct))
{
    // Multiple readers permitted
}

await using (await rwMutex.LockAsync(ct))
{
    // Exclusive writer
}
```

### RwMutex notes

- Writer acquisition blocks new readers until released.
- Cancellation of a pending reader/writer propagates `OperationCanceledException`.

## Channels

`Go.MakeChannel<T>` creates unbounded or bounded channels for message passing. Fluent builders provide DI-friendly factories when you need to customise options once and share the channel through dependency injection.

### Overloads

- `MakeChannel<T>(int? capacity = null)`
- `MakeChannel<T>(BoundedChannelOptions options)`
- `MakePrioritizedChannel<T>(int priorityLevels, int defaultPriority)`
- `Go.BoundedChannel<T>(int capacity)`
- `Go.PrioritizedChannel<T>()` / `Go.PrioritizedChannel<T>(int priorityLevels)`

### Channel usage

```csharp
var channel = Go.MakeChannel<string>(capacity: 32);
await channel.Writer.WriteAsync("message", ct);
var value = await channel.Reader.ReadAsync(ct);
```

### Channel notes

- Bounded channels respect `FullMode` from `BoundedChannelOptions`.
- `TryComplete(Exception?)` propagates faults to readers.

### Channel builders

```csharp
services.AddBoundedChannel<Job>(capacity: 64, builder => builder
    .SingleWriter()
    .WithFullMode(BoundedChannelFullMode.DropOldest));

services.AddPrioritizedChannel<Job>(priorityLevels: 3, builder => builder
    .WithCapacityPerLevel(32)
    .WithPrefetchPerPriority(2)
    .WithDefaultPriority(1));
```

- `AddBoundedChannel<T>` registers a `Channel<T>` alongside its reader and writer.
- `AddPrioritizedChannel<T>` registers `PrioritizedChannel<T>`, the prioritized reader/writer helpers, and the base `ChannelReader<T>`/`ChannelWriter<T>` facades.
- Prioritized channels prefetch at most `PrefetchPerPriority` items per lane (default 1). Tune it through `PrioritizedChannelOptions.PrefetchPerPriority` or `WithPrefetchPerPriority` to balance throughput against backpressure.
- **Perf tip:** When only one consumer drains the channel, set `SingleReader = true` (or call `SingleReader()` on the builder). Hugo then uses a lightweight per-lane buffer instead of multi-producer queues, reducing allocations and contention while remaining Native AOT friendly. Keep it `false` if multiple readers may observe the channel.
- The prioritized reader’s slow path now reuses wait registrations instead of `Task.WhenAny` arrays, so `WaitToReadAsync` stays effectively allocation-free when lanes run hot.
- Exceptions or cancellations from individual priority lanes are observed immediately and surfaced through the unified reader, preventing `UnobservedTaskException` warnings when a single lane faults.
- Builders expose `.Build()` when you need an inline channel instance without DI.

## Task queue leasing

`TaskQueue<T>` layers cooperative leasing semantics on top of channels. Producers enqueue work items, workers lease them for a configurable duration, and can heartbeat, complete, or fail each lease. Options configure queue naming, buffer size, lease duration, heartbeat cadence, sweep interval, requeue delay, maximum delivery attempts, and optional backpressure callbacks.

### TaskQueue usage

```csharp
var options = new TaskQueueOptions
{
    Name = "telemetry-queue",
    LeaseDuration = TimeSpan.FromSeconds(10),
    HeartbeatInterval = TimeSpan.FromSeconds(2),
    RequeueDelay = TimeSpan.FromMilliseconds(250),
    Backpressure = new TaskQueueBackpressureOptions
    {
        HighWatermark = 256,
        LowWatermark = 64,
        Cooldown = TimeSpan.FromSeconds(5),
        StateChanged = state =>
        {
            if (state.IsActive)
            {
                logger.LogWarning("Throttling appends at depth {Depth}", state.PendingCount);
            }
            else
            {
                logger.LogInformation("Backpressure cleared at depth {Depth}", state.PendingCount);
            }
        }
    }
};

await using var queue = new TaskQueue<Job>(options);

await queue.EnqueueAsync(new Job("alpha"), ct);
var lease = await queue.LeaseAsync(ct);

try
{
    logger.LogDebug("Ownership token {Token}", lease.OwnershipToken);
    await ProcessAsync(lease.Value, ct);
    await lease.CompleteAsync(ct);
}
catch (Exception ex)
{
    await lease.FailAsync(Error.FromException(ex), requeue: true, ct);
}
```

When draining a node for maintenance, capture the outstanding work and restore it on the new process:

```csharp
IReadOnlyList<TaskQueuePendingItem<Job>> pending = await queue.DrainPendingItemsAsync(ct);
await durableStore.SaveAsync(pending, ct);

// Later, or on another instance
await queue.RestorePendingItemsAsync(await durableStore.LoadAsync(ct), ct);
```

### TaskQueue notes

- Heartbeats extend the lease without handing work to another worker while still respecting `HeartbeatInterval` throttling.
- `FailAsync` captures the provided `Error`, increments the attempt, and either requeues or dead-letters when `MaxDeliveryAttempts` is exceeded.
- Expired leases are detected by a background sweep and automatically requeued with an `error.taskqueue.lease_expired` payload.
- Every lease exposes a monotonic `OwnershipToken` (`sequence`, `attempt`, `leaseId`) that you can log or persist as a fencing token.
- `DrainPendingItemsAsync`/`RestorePendingItemsAsync` make rolling upgrades safe by snapshotting in-flight work without losing metadata.
- `ConfigureBackpressure` or `TaskQueueBackpressureMonitor<T>` keep backlog transitions observable without recreating queues; `QueueName` exposes the resolved name for diagnostics.

### Task queue health checks

Hook `TaskQueueHealthCheck<T>` into ASP.NET Core health probes to block rollouts whenever backlog exceeds a threshold:

```csharp
builder.Services.AddTaskQueueHealthCheck<Job>(
    name: "job-queue",
    queueFactory: sp => sp.GetRequiredService<TaskQueue<Job>>(),
    configure: options =>
    {
        options.PendingDegradedThreshold = 512;
        options.PendingUnhealthyThreshold = 1024;
        options.ActiveLeaseDegradedThreshold = 32;
    });
```

Expose `/health/ready` via `app.MapHealthChecks` so orchestrators wait for pending work to drain before terminating replicas.

### TaskQueue backpressure monitor

`TaskQueueBackpressureMonitor<T>` exports SafeTaskQueue-compatible backpressure signals, rate limiter switches, diagnostics channels, and `WaitForDrainingAsync` helpers so producers can await relief before enqueueing more work. Monitors reconfigure the queue’s backpressure thresholds at runtime and publish typed `TaskQueueBackpressureSignal` instances whenever depth crosses the configured watermarks.

```csharp
await using var queue = new TaskQueue<Job>(new TaskQueueOptions { Name = "telemetry", Capacity = 2048 });
await using var monitor = new TaskQueueBackpressureMonitor<Job>(queue, new TaskQueueBackpressureMonitorOptions
{
    HighWatermark = 512,
    LowWatermark = 128,
    Cooldown = TimeSpan.FromSeconds(5)
});

await using var diagnostics = new TaskQueueBackpressureDiagnosticsListener(capacity: 256);
using var diagnosticsSubscription = monitor.RegisterListener(diagnostics);

var limiter = new BackpressureAwareRateLimiter(
    unthrottledLimiter: new ConcurrencyLimiter(new ConcurrencyLimiterOptions(permitLimit: 256, queueLimit: 0, queueProcessingOrder: QueueProcessingOrder.OldestFirst)),
    backpressureLimiter: new ConcurrencyLimiter(new ConcurrencyLimiterOptions(permitLimit: 16, queueLimit: 512, queueProcessingOrder: QueueProcessingOrder.OldestFirst)),
    disposeUnthrottledLimiter: true,
    disposeBackpressureLimiter: true);
using var limiterSubscription = monitor.RegisterListener(limiter);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.Get("taskqueue", _ => limiter.LimiterSelector()));
});

app.UseRateLimiter();
app.MapGet("/control-plane/backpressure", async context =>
{
    await foreach (var signal in diagnostics.Reader.ReadAllAsync(context.RequestAborted))
    {
        await context.Response.WriteAsJsonAsync(signal, context.RequestAborted);
    }
});

// Enqueueers can await relief instead of blindly retrying.
if (monitor.IsActive)
{
    await monitor.WaitForDrainingAsync(ct);
}
```

Metrics emitted via `GoDiagnostics` include `hugo.taskqueue.backpressure.active` (up/down gauge per queue), `hugo.taskqueue.backpressure.pending` (histogram of pending depth), `hugo.taskqueue.backpressure.transitions` (counter), and `hugo.taskqueue.backpressure.duration` (histogram of state durations). Attach a `TaskQueueBackpressureDiagnosticsListener` to expose current/streamed signals over HTTP or gRPC for control-plane automation.

### TaskQueueChannelAdapter usage

```csharp
await using var queue = new TaskQueue<EventPayload>();
await using var adapter = TaskQueueChannelAdapter<EventPayload>.Create(queue, concurrency: 4);

await queue.EnqueueAsync(payload, ct);

await foreach (var lease in adapter.Reader.ReadAllAsync(ct))
{
    try
    {
        await HandleAsync(lease.Value, ct);
        await lease.CompleteAsync(ct);
    }
    catch (Exception ex)
    {
        await lease.FailAsync(Error.FromException(ex), requeue: true, ct);
    }
}
```

### TaskQueueChannelAdapter notes

- Pumps run in the background and publish leases to the channel reader. If the channel is closed or cancellation triggers before delivery, the adapter requeues the lease with `Error.Canceled` metadata.
- `concurrency` controls both the number of pumps and the number of outstanding leases; the default channel is bounded to this limit so slow consumers cannot cause `_queue._leases` to grow unchecked. Provide a custom bounded `Channel<TaskQueueLease<T>>` if you need different buffering semantics.
- Disposing the adapter waits for pumps to finish; when `ownsQueue` is `true`, the underlying queue is disposed as well.

### SafeTaskQueueWrapper usage

`SafeTaskQueueWrapper<T>` converts the exception-heavy surface of `TaskQueue<T>` into `Result<Unit>` and `Result<SafeTaskQueueLease<T>>` responses so producers and consumers can branch without try/catch blocks.

```csharp
await using var queue = new TaskQueue<Job>();
await using var safeQueue = new SafeTaskQueueWrapper<Job>(queue);

var enqueue = await safeQueue.EnqueueAsync(new Job("alpha"), ct);
if (enqueue.IsFailure)
{
    logger.LogWarning("Queue enqueue failed: {Error}", enqueue.Error);
    return;
}

Result<SafeTaskQueueLease<Job>> leaseResult = await safeQueue.LeaseAsync(ct);
if (leaseResult.IsFailure)
{
    logger.LogWarning("Lease failed: {Error}", leaseResult.Error);
    return;
}

SafeTaskQueueLease<Job> lease = leaseResult.Value;
var complete = await lease.CompleteAsync(ct);
if (complete.IsFailure)
{
    logger.LogWarning("Completion failed: {Error}", complete.Error);
}
```

### SafeTaskQueueWrapper notes

- `EnqueueAsync` and `LeaseAsync` translate `OperationCanceledException`, `ObjectDisposedException`, and other failures into structured `Error` codes such as `error.taskqueue.disposed`.
- `Wrap(TaskQueueLease<T>)` adapts existing leases (for example those surfaced by `TaskQueueChannelAdapter`) into `SafeTaskQueueLease<T>` instances.
- `DisposeAsync` optionally disposes the underlying queue when `ownsQueue` is `true`.
- `SafeTaskQueueLease<T>` methods (`CompleteAsync`, `HeartbeatAsync`, `FailAsync`) return `Result<Unit>` and normalise cancellations and inactive lease states to `error.taskqueue.lease_inactive`.
- `SafeTaskQueueLease<T>` surfaces the underlying `SequenceId` and `OwnershipToken` so producers/consumers can log the same fencing metadata as the raw lease.

## Select helpers

Await whichever channel case becomes ready first.

### Select APIs

- `Go.SelectAsync<TResult>(params ChannelCase<TResult>[] cases)` to await the first ready case.
- `Go.SelectAsync<TResult>(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default, params ChannelCase<TResult>[] cases)` for deadline-aware selects.
- `Go.Select<TResult>(TimeProvider? provider = null, CancellationToken cancellationToken = default)` / `Go.Select<TResult>(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default)` fluent builders.
- `ChannelCase.Create<T, TResult>(ChannelReader<T>, Func<T, CancellationToken, ValueTask<Result<TResult>>>)` plus overloads for ValueTask callbacks and `ChannelCase.CreateDefault<TResult>(...)`.

### Select example

```csharp
var result = await Go.SelectAsync(
    timeout: TimeSpan.FromSeconds(1),
    cases: new[]
    {
        ChannelCase.Create(textChannel.Reader, async (text, ct) =>
        {
            Console.WriteLine($"text: {text}");
            return Result.Ok(Go.Unit.Value);
        }),
        ChannelCase.Create(signal.Reader, (unit, _) =>
        {
            Console.WriteLine("signal received");
            return Task.FromResult(Result.Ok(Go.Unit.Value));
        })
    },
    cancellationToken: ct);
```

### Select notes

- Diagnostics capture attempts, completions, latency, cancellations.
- Timeouts use `TimeProvider` when supplied.
- Cancelled tokens surface as `Error.Canceled` with originating token metadata.
- `SelectBuilder` supports `.Default(...)` fallbacks, per-case `priority` ordering, and `.Deadline(...)` helpers for timer-driven outcomes.

## Fan-in utilities

- `Go.SelectFanInAsync(...)` repeatedly selects until all cases complete, returning `Result<Unit>` so caller code can surface errors. When you pass a timeout the helper captures a single absolute deadline (using the supplied `TimeProvider` when present) and enforces it across the entire fan-in session rather than per-iteration waits. Use `Go.SelectFanInValueTaskAsync(...)` when your continuations already return `ValueTask` so the loop can stay allocation free.
- `Go.FanInAsync(IEnumerable<ChannelReader<T>>, ChannelWriter<T>, bool completeDestination, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)` merges multiple readers into an existing writer and returns `Result<Unit>`.
- `Go.FanIn(IEnumerable<ChannelReader<T>>, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)` returns a new reader and internally manages the destination channel lifecycle.

See [Coordinate fan-in workflows](../how-to/fan-in-channels.md) for step-by-step usage.

## Result orchestration helpers

- `Go.FanOutAsync(IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations, ResultExecutionPolicy? policy = null, CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)` executes delegates concurrently and aggregates values through `Result.WhenAll`. Prefer `Go.FanOutValueTaskAsync(IEnumerable<Func<CancellationToken, ValueTask<Result<T>>>> ...)` when your delegates already return `ValueTask<Result<T>>`.
- `Go.RaceAsync(IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations, ResultExecutionPolicy? policy = null, CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)` returns the first successful result (`Result.WhenAny` under the covers) and compensates secondary successes. Use `Go.RaceValueTaskAsync(...)` for ValueTask-based delegates.
- `Go.WithTimeoutAsync(Func<CancellationToken, Task<Result<T>>> operation, TimeSpan timeout, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)` produces `Error.Timeout` when the deadline elapses, returns `Error.Canceled` if the supplied token fires first, otherwise forwards the inner result. `Go.WithTimeoutValueTaskAsync(...)` mirrors the behavior for ValueTask-returning delegates.
- `Go.RetryAsync(Func<int, CancellationToken, Task<Result<T>>> operation, int maxAttempts = 3, TimeSpan? initialDelay = null, TimeProvider? timeProvider = null, ILogger? logger = null, CancellationToken cancellationToken = default)` applies exponential backoff using `Result.RetryWithPolicyAsync`, propagates structured retry metadata, and halts immediately when the delegate throws or returns an `Error.Canceled`, regardless of which linked token triggered it. Reach for `Go.RetryValueTaskAsync(...)` when the retried delegate returns `ValueTask<Result<T>>`.

## Timers

Timer primitives mirror Go semantics while honouring `TimeProvider`.

### Timer APIs

- `Go.DelayAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)`
- `Go.After(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)`
- `Go.AfterAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)`
- `Go.AfterValueTaskAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)`
- `Go.NewTicker(TimeSpan period, TimeProvider? provider = null, CancellationToken cancellationToken = default)`
- `Go.Tick(TimeSpan period, TimeProvider? provider = null, CancellationToken cancellationToken = default)`

### Timer example

```csharp
var provider = new FakeTimeProvider();

var once = await Go.AfterAsync(TimeSpan.FromSeconds(5), provider, ct);

await using var ticker = Go.NewTicker(TimeSpan.FromSeconds(1), provider, ct);
var tick = await ticker.ReadAsync(ct);
```

### Timer notes

- `GoTicker.Stop()` / `StopAsync()` dispose timers and complete the channel.
- Use fake time providers in tests for deterministic scheduling.

## Deterministic utilities

These helpers keep workflow-style orchestrations deterministic across replays by persisting decisions externally.

- `VersionGate` records version markers for long-lived change management using optimistic inserts. Call `Require(changeId, minVersion, maxVersion)` to retrieve the persisted version or create one deterministically. Supply a custom provider when you need to phase rollouts; concurrent writers that lose the insert return `error.version.conflict` so callers can fallback.
- `DeterministicEffectStore` captures side-effect results once and replays them. Use a durable implementation of `IDeterministicStateStore` in production and the provided `InMemoryDeterministicStateStore` inside tests.

### VersionGate usage

```csharp
var store = new InMemoryDeterministicStateStore();
var gate = new VersionGate(store);

var decision = gate.Require("workflow.stepA", VersionGate.DefaultVersion, maxSupportedVersion: 2);
if (decision.IsFailure)
{
    return decision.Error!;
}

switch (decision.Value.Version)
{
    case VersionGate.DefaultVersion:
        return await RunLegacyAsync(ct);
    case 2:
        return await RunV2Async(ct);
    default:
        throw new InvalidOperationException();
}
```

### DeterministicEffectStore usage

```csharp
var effectStore = new DeterministicEffectStore(store);

var payload = await effectStore.CaptureAsync("side-effect.payment", async token =>
{
    var response = await httpClient.PostAsync("/payments", content, token);
    return response.IsSuccessStatusCode
        ? Result.Ok(await response.Content.ReadFromJsonAsync<Receipt>(cancellationToken: token))
        : Result.Fail<Receipt>(Error.From("payment failed", ErrorCodes.Validation));
});

if (payload.IsFailure)
{
    return payload.Error!;
}

return payload.Value;
```
