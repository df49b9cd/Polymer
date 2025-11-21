# Result Pipeline Reference

`Result<T>` is the primary abstraction for representing success or failure. This reference enumerates the factory helpers, combinators, orchestration utilities, and metadata APIs that shape railway-oriented workflows.

## Quick navigation

- [Creating results](#creating-results)
- [Inspecting and extracting state](#inspecting-and-extracting-state)
- [Synchronous combinators](#synchronous-combinators)
- [Async combinators](#async-combinators)
- [Collection helpers](#collection-helpers)
- [Streaming and channels](#streaming-and-channels)
- [Parallel orchestration and retries](#parallel-orchestration-and-retries)
- [Error metadata](#error-metadata)
- [Cancellation handling](#cancellation-handling)
- [Diagnostics](#diagnostics)

> **Best practice:** Keep cancellations (`Error.Canceled`) flowing as data—avoid catching `OperationCanceledException` unless you intend to translate it to a different error code.

## Creating results

- `Result.Ok<T>(T value)` / `Go.Ok(value)` wrap the value in a success.
- `Result.Fail<T>(Error? error)` / `Go.Err<T>(Error? error)` create failures (null defaults to `Error.Unspecified`). `Go.Err<T>(string message, string? code = null)` and `Go.Err<T>(Exception exception, string? code = null)` shortcut common cases.
- `Result.FromOptional<T>(Optional<T> optional, Func<Error> errorFactory)` lifts optionals into results.
- `Result.Try(Func<T> operation, Func<Exception, Error?>? errorFactory = null)` and `Result.TryAsync(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default, Func<Exception, Error?>? errorFactory = null)` capture exceptions as `Error` values (cancellations become `Error.Canceled`).
- `result.WithCompensation(Func<CancellationToken, ValueTask> compensation)` (or the overload that captures state) attaches rollback actions to a result. Pipeline orchestrators such as `ResultPipelineChannels.SelectAsync` absorb these scopes automatically so failures later in the pipeline execute the recorded cleanup.

```csharp
var ok = Go.Ok(42);
var failure = Go.Err<int>("validation failed", ErrorCodes.Validation);
var hydrated = await Result.TryAsync(ct => repository.LoadAsync(id, ct), ct);
var forced = Result.FromOptional(Optional<string>.None(), () => Error.From("missing", ErrorCodes.Validation));
```

## Inspecting and extracting state

- `result.IsSuccess` / `result.IsFailure`
- `result.TryGetValue(out T value)` / `result.TryGetError(out Error error)`
- `result.Switch(Action<T> onSuccess, Action<Error> onFailure)` and `result.Match<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onFailure)`
- `result.SwitchAsync(...)` / `result.MatchAsync(...)`
- `result.ValueOr(T fallback)` / `result.ValueOr(Func<Error, T> factory)` / `result.ValueOrThrow()` (throws `ResultException`)
- `result.ToOptional()` converts to `Optional<T>`; tuple deconstruction `(value, error)` is supported for legacy interop.

## Synchronous combinators

- Execution flow: `Functional.Then`, `Functional.Recover`, `Functional.Finally`
- Mapping & side-effects: `Functional.Map`, `Functional.Tap`, `Functional.Tee`, `Functional.OnSuccess`, `Functional.OnFailure`, `Functional.TapError`
- Validation: `Functional.Ensure` (and LINQ aliases `Where`, `Select`, `SelectMany`)

```csharp
var outcome = Go.Ok(request)
    .Ensure(r => !string.IsNullOrWhiteSpace(r.Email))
    .Then(SendEmail)
    .Tap(response => audit.Log(response))
    .Map(response => response.MessageId)
    .Recover(_ => Go.Ok("fallback"));
```

## Async combinators

Every async variation accepts a `CancellationToken` and normalises cancellations to `Error.Canceled`. Each helper also ships `*ValueTaskAsync` overloads, so `ValueTask<Result<T>>` sources and delegates compose without forcing an extra `Task` allocation.

- Execution flow: `Functional.ThenAsync` overloads bridge sync→async, async→sync, and async→async pipelines. `Functional.ThenValueTaskAsync` mirrors the same combinations for ValueTask-based sources or continuations.
- Mapping & instrumentation: `Functional.MapAsync` transforms values with synchronous or asynchronous mappers, while `Functional.MapValueTaskAsync` keeps ValueTask-returning mappers allocation-free.
- Side-effects and notifications: `Functional.TapAsync` / `Functional.TeeAsync` execute side-effects without altering the pipeline. `Functional.OnSuccessAsync`, `Functional.OnFailureAsync`, and `Functional.TapErrorAsync` target lifecycle-specific side-effects. Their `*ValueTaskAsync` companions (`TapValueTaskAsync`, `TeeValueTaskAsync`, `OnSuccessValueTaskAsync`, `OnFailureValueTaskAsync`, `TapErrorValueTaskAsync`) accept ValueTask-returning delegates.
- Recovery: `Functional.RecoverAsync` retries failures with synchronous or asynchronous recovery logic; `Functional.RecoverValueTaskAsync` takes the same inputs when the recovery delegate already returns `ValueTask<Result<T>>`.
- Validation: `Functional.EnsureAsync` validates successful values asynchronously. Use `Functional.EnsureValueTaskAsync` to keep validation logic in `ValueTask`.
- Cleanup/finalisation: `Functional.FinallyAsync` awaits success/failure continuations (sync or async callbacks). `Functional.FinallyValueTaskAsync` allows both the source and the continuations to stay on `ValueTask`.

## Collection helpers

- `Result.Sequence` / `Result.SequenceAsync` aggregate successes from `IEnumerable<Result<T>>` and `IAsyncEnumerable<Result<T>>`.
- `Result.Traverse` / `Result.TraverseAsync` project values through selectors that return `Result<T>`.
- `Result.Group`, `Result.Partition`, and `Result.Window` reshape collections while short-circuiting on the first failure.
- `Result.MapStreamAsync` projects asynchronous streams into result streams, aborting after the first failure.

All helpers propagate the first encountered error and respect cancellation tokens.

## Streaming and channels

- `Result.MapStreamAsync<TIn, TOut>` projects `IAsyncEnumerable<TIn>` into `IAsyncEnumerable<Result<TOut>>`, halting on the first failure. Use the overload that accepts `ValueTask<Result<TOut>>` to minimize allocations.
- `Result.FlatMapStreamAsync<TIn, TOut>` (select-many) projects each source item into an async enumerable of `Result<TOut>` and flattens the successes/failures into a single stream.
- `Result.FilterStreamAsync<T>` drops successful values that fail your predicate while passing failures through untouched.
- `IAsyncEnumerable<Result<T>>.ForEachAsync`, `.ForEachLinkedCancellationAsync`, `.TapSuccessEachAsync`, `.TapFailureEachAsync`, and `.CollectErrorsAsync` provide fluent consumption patterns (per-item side effects, per-item cancellation tokens, error aggregation).
- Use `TapSuccessEachAggregateErrorsAsync` / `TapFailureEachAggregateErrorsAsync` to traverse the whole stream while surfacing an aggregate error, or `TapSuccessEachIgnoreErrorsAsync` / `TapFailureEachIgnoreErrorsAsync` when callers must always receive success but you still want per-item taps to run.
- `IAsyncEnumerable<Result<T>>.ToChannelAsync(ChannelWriter<Result<T>> writer, CancellationToken)` and `ChannelReader<Result<T>>.ReadAllAsync(CancellationToken)` bridge result streams with `System.Threading.Channels`.
- `Result.FanInAsync` / `Result.FanOutAsync` merge or broadcast result streams across channel writers.
- `Result.WindowAsync` batches successful values into fixed-size windows; `Result.PartitionAsync` splits streams using a predicate.

Writers are completed automatically (with the originating error when appropriate) to prevent consumer deadlocks.

### Pipeline-aware channel adapters

- `ResultPipelineChannels.SelectAsync` mirrors `Go.SelectAsync` but links into the active `ResultPipelineStepContext`, automatically absorbing any compensations attached via `Result<T>.WithCompensation(...)` so failures later in the pipeline execute the recorded rollback actions.
- `ResultPipelineChannels.Select<TResult>(...)` exposes a pipeline-aware `SelectBuilder<TResult>` for fluent fan-in workflows without rewriting cancellation/compensation plumbing.
- `ResultPipelineChannels.FanInAsync` exposes the same overload set as `Go.SelectFanIn*`, wrapping each handler with the pipeline’s cancellation token, time provider, and compensation scope.
- `ResultPipelineChannels.MergeAsync`, `MergeWithStrategyAsync`, and `BroadcastAsync` / `FanOut` forward to the Go helpers while preserving pipeline diagnostics and compensation scopes.
- `ResultPipelineChannels.WindowAsync` produces `ChannelReader<IReadOnlyList<T>>` outputs that flush when a batch size or flush interval (or both) is reached, using the pipeline’s `TimeProvider` for deterministic timers.

## Parallel orchestration and retries

- `Result.WhenAll` executes result-aware operations concurrently, applying the supplied `ResultExecutionPolicy` (retries + compensation) to each step. When cancellation interrupts execution—even if `Task.WhenAll` short-circuits with `OperationCanceledException`—previously completed operations have their compensation scopes replayed before the aggregated result returns `Error.Canceled`, so side effects are rolled back deterministically.
- `Result.WhenAny` resolves once the first success arrives, compensating secondary successes and aggregating errors when every branch fails.
- `Result.RetryWithPolicyAsync` runs a delegate under a retry/compensation policy, surfacing structured failure metadata when attempts are exhausted.
- `ResultPipeline.FanOutAsync` / `ResultPipeline.RaceAsync` are thin wrappers over `Result.WhenAll` / `Result.WhenAny` that return `ValueTask<Result<T>>` to match the Go helpers without losing pipeline metadata.
- `ResultPipeline.RetryAsync` mirrors `Go.RetryAsync` for pipeline-aware delegates, wiring optional loggers and exponential backoff hints into `ResultExecutionBuilders.ExponentialRetryPolicy`.
- `ResultPipeline.WithTimeoutAsync` enforces deadlines with the same `TimeProvider` semantics as Go timers while ensuring compensations registered by the timed operation run before surfacing `Error.Timeout`.
- `ResultPipelineWaitGroupExtensions.Go` and `ResultPipelineErrGroupExtensions.Go` bridge Go-style coordination primitives with result pipelines so background fan-out work participates in the same compensation scope.
- `ResultPipelineTimers.DelayAsync`, `AfterAsync`, `NewTicker`, and `Tick` reuse the pipeline `TimeProvider`, linking cancellation tokens automatically and registering compensations to dispose timers/tickers deterministically.
- `Result.TieredFallbackAsync` evaluates `ResultFallbackTier<T>` instances sequentially; strategies within a tier run concurrently and cancel once a peer succeeds. Metadata keys (`fallbackTier`, `tierIndex`, `strategyIndex`) are attached to failures for observability.
- `ResultFallbackTier<T>.From(...)` adapts synchronous or asynchronous delegates into tier definitions without manually handling `ResultPipelineStepContext`.

### Tiered fallback example

```csharp
var policy = ResultExecutionPolicy.None.WithRetry(
    ResultRetryPolicy.Exponential(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(200)));

var tiers = new[]
{
    ResultFallbackTier<HttpResponseMessage>.From(
        "primary",
        ct => TrySendAsync(primaryClient, payload, ct)),
    new ResultFallbackTier<HttpResponseMessage>(
        "regional",
        new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<HttpResponseMessage>>>[]
        {
            (ctx, ct) => TrySendAsync(euClient, payload, ct),
            (ctx, ct) => TrySendAsync(apacClient, payload, ct)
        })
};

var response = await Result.TieredFallbackAsync(tiers, policy, cancellationToken);

if (response.IsFailure && response.Error!.Metadata.TryGetValue("fallbackTier", out var tier))
{
    logger.LogWarning("All strategies in tier {Tier} failed: {Error}", tier, response.Error);
}
```

### ErrGroup integration

```csharp
using var group = new ErrGroup();
var retryPolicy = ResultExecutionPolicy.None.WithRetry(
    ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)));

group.Go((ctx, ct) =>
{
    return Result.RetryWithPolicyAsync(async (_, token) =>
    {
        var response = await client.SendAsync(request, token);
        return response.IsSuccessStatusCode
            ? Result.Ok(Go.Unit.Value)
            : Result.Fail<Unit>(Error.From("HTTP failure", ErrorCodes.Validation));
    }, retryPolicy, ct, ctx.TimeProvider);
}, stepName: "ship-order", policy: retryPolicy);

var completion = await group.WaitAsync(cancellationToken);
if (completion.IsFailure && completion.Error?.Code == ErrorCodes.Canceled)
{
    // Handle the aborted pipeline (e.g., user-initiated cancellation) and exit early.
}
else
{
    completion.ValueOrThrow();
}
```

Reusing the same `ErrGroup` instance outside of its `using` scope is unsupported. Once disposed, any `Go(...)` call throws `ObjectDisposedException`, while the exposed `Token` remains valid for listeners already awaiting cancellation.
Manual calls to `Cancel()` record `Error.Canceled` before the linked `CancellationTokenSource` is signaled, so `WaitAsync` deterministically returns `Result.Fail<Unit>` and `ErrGroup.Error` surfaces the same payload.
Policy-backed `Go(...)` overloads now cancel peer operations as soon as a failure is captured—before compensation handlers execute—so slow cleanup work cannot mask cancellation from the remaining steps.

## Error metadata

- `Error.WithMetadata(string key, object? value)` / `Error.WithMetadata(IEnumerable<KeyValuePair<string, object?>> metadata)`
- `Error.TryGetMetadata<T>(string key, out T value)`
- `Error.WithCode(string? code)` / `Error.WithCause(Exception? cause)`
- Factory helpers: `Error.From`, `Error.FromException`, `Error.Canceled`, `Error.Timeout`, `Error.Unspecified`, `Error.Aggregate`
- `ErrorCodes.Descriptors` exposes compile-time generated metadata for all well-known error codes so logs, dashboards, and validation share a single source of truth. Call `ErrorCodes.TryGetDescriptor(code, out var descriptor)` or `ErrorCodes.GetDescriptor(code)` to enrich observability pipelines with descriptions and categories. When a known code is assigned, Hugo automatically attaches `error.name`, `error.description`, and `error.category` metadata entries to the resulting <code>Error</code>.

```csharp
var result = Go.Ok(user)
    .Ensure(
        predicate: u => u.Age >= 18,
        errorFactory: u => Error.From("age must be >= 18", ErrorCodes.Validation)
            .WithMetadata("age", u.Age)
            .WithMetadata("userId", u.Id));

if (result.IsFailure && result.Error!.TryGetMetadata<int>("age", out var age))
{
    logger.LogWarning("Rejected under-age user {UserId} ({Age})", result.Error.Metadata["userId"], age);
}
```

## Cancellation handling

- `Error.Canceled` represents cancellation captured via Hugo APIs and carries the originating token (when available) under `"cancellationToken"`.
- Async combinators convert `OperationCanceledException` into `Error.Canceled` so downstream callers can branch consistently.

## Diagnostics

When `GoDiagnostics` is configured, result creation increments:

- `result.successes`
- `result.failures`

Side-effect helpers such as `TapError` also contribute to `result.failures`, making it easy to correlate result pipelines with observability platforms.
