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
- `WaitGroup.Go(Func<Task> work, CancellationToken cancellationToken = default)` (runs the delegate via `Task.Run`)
- `WaitGroup.Done()`
- `WaitGroup.WaitAsync(CancellationToken cancellationToken = default)`
- `WaitGroup.WaitAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default)` (returns `bool`)
- `GoWaitGroupExtensions.Go(Func<CancellationToken, Task> work, CancellationToken cancellationToken)` when you prefer to pass an explicit token

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
    .WithDefaultPriority(1));
```

- `AddBoundedChannel<T>` registers a `Channel<T>` alongside its reader and writer.
- `AddPrioritizedChannel<T>` registers `PrioritizedChannel<T>`, the prioritized reader/writer helpers, and the base `ChannelReader<T>`/`ChannelWriter<T>` facades.
- Builders expose `.Build()` when you need an inline channel instance without DI.

## Task queue leasing

`TaskQueue<T>` layers cooperative leasing semantics on top of channels. Producers enqueue work items, workers lease them for a configurable duration, and can heartbeat, complete, or fail each lease. Options configure buffer size, lease duration, heartbeat cadence, sweep interval, requeue delay, and maximum delivery attempts.

### TaskQueue usage

```csharp
var options = new TaskQueueOptions
{
    LeaseDuration = TimeSpan.FromSeconds(10),
    HeartbeatInterval = TimeSpan.FromSeconds(2),
    RequeueDelay = TimeSpan.FromMilliseconds(250)
};

await using var queue = new TaskQueue<Job>(options);

await queue.EnqueueAsync(new Job("alpha"), ct);
var lease = await queue.LeaseAsync(ct);

try
{
    await ProcessAsync(lease.Value, ct);
    await lease.CompleteAsync(ct);
}
catch (Exception ex)
{
    await lease.FailAsync(Error.FromException(ex), requeue: true, ct);
}
```

### TaskQueue notes

- Heartbeats extend the lease without handing work to another worker while still respecting `HeartbeatInterval` throttling.
- `FailAsync` captures the provided `Error`, increments the attempt, and either requeues or dead-letters when `MaxDeliveryAttempts` is exceeded.
- Expired leases are detected by a background sweep and automatically requeued with an `error.taskqueue.lease_expired` payload.

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
- `concurrency` controls the number of concurrent pumps issuing leases.
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

## Select helpers

Await whichever channel case becomes ready first.

### Select APIs

- `Go.SelectAsync(params ChannelCase[] cases)` to await the first ready case.
- `Go.SelectAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default, params ChannelCase[] cases)` for deadline-aware selects.
- `Go.Select<TResult>(TimeProvider? provider = null, CancellationToken cancellationToken = default)` / `Go.Select<TResult>(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default)` fluent builders.
- `ChannelCase.Create<T>(ChannelReader<T>, Func<T, CancellationToken, Task<Result<Unit>>>)` plus overloads for tasks/actions and `ChannelCase.CreateDefault(...)`.

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

- `VersionGate` records version markers for long-lived change management. Call `Require(changeId, minVersion, maxVersion)` to retrieve the persisted version or create one deterministically. Supply a custom provider when you need to phase rollouts.
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
