# Deterministic Coordination Reference

Deterministic coordination stitches together version markers and replay-safe side effects. Use these primitives when evolving long-running workflows without duplicating work on retries or replays.

## Quick navigation

- [Components](#components)
- [One-shot execution](#one-shot-execution)
- [Workflow builder](#workflow-builder)
- [Capturing deterministic steps](#capturing-deterministic-steps)
- [Error behaviour](#error-behaviour)
- [Observability](#observability)

> **Best practice:** Persist deterministic state in durable storage (SQL, Cosmos DB, Redis) before performing out-of-process side effects so replays can resume from the same step without re-executing external calls.

## Components

- `VersionGate` records an immutable version decision per change identifier using optimistic inserts (`IDeterministicStateStore.TryAdd`). Concurrent writers that lose the CAS receive `error.version.conflict` metadata so callers can retry or fallback deterministically.
- `DeterministicEffectStore` captures idempotent side effects keyed by change/version/scope.
- `DeterministicGate` combines both to execute code paths safely across replays.

## One-shot execution

The simplest overload mirrors the original upgraded/legacy split:

```csharp
var outcome = await gate.ExecuteAsync(
    changeId: "change.v2",
    minVersion: 1,
    maxVersion: 3,
    upgraded: ct => processor.RunV3Async(ct),
    legacy: ct => processor.RunV1Async(ct),
    initialVersionProvider: _ => 2,
    cancellationToken: ct);
```

`DeterministicGate` persists the max version path by default. Subsequent executions reuse the original result by replaying the captured effect via `DeterministicEffectStore`.

## Workflow builder

`DeterministicGate.Workflow<TResult>` produces a branch builder for richer coordination. Configure predicates in declaration order and optionally supply a fallback:

```csharp
var workflow = gate.Workflow<int>("customer.migration", 1, 3, _ => 2)
    .For(decision => decision.IsNew, (ctx, ct) => ctx.CaptureAsync("init", _ => Task.FromResult(Result.Ok(0)), ct))
    .ForVersion(2, async (ctx, ct) =>
    {
        await ctx.CaptureAsync("upgrade-db", token => migrator.RunAsync(ctx.Version, token), ct);
        return Result.Ok(42);
    })
    .WithFallback((ctx, ct) => ctx.CaptureAsync("noop", () => Result.Ok(-1), ct));

var result = await workflow.ExecuteAsync(ct);
```

- `ForVersion(version, ...)` targets an exact version.
- `ForRange(minVersion, maxVersion, ...)` targets an inclusive range.
- `For(predicate, ...)` runs when the predicate matches the current `VersionDecision`.
- `WithFallback(...)` executes when no branch qualifies.

Branches and the fallback always execute inside the deterministic effect envelope supplied by `DeterministicEffectStore`, so a replay reuses the stored result.

## Capturing deterministic steps

Inside a branch the provided `DeterministicWorkflowContext` exposes:

- `ctx.Version`, `ctx.IsNew`, and `ctx.RecordedAt` describing the decision.
- `ctx.CreateEffectId("step")` for manual identifiers.
- `ctx.CaptureAsync(stepId, effect, cancellationToken)` helpers that scope deterministic side effects under `changeId::v{version}::{stepId}`.

Example step capture:

```csharp
var response = await ctx.CaptureAsync(
    stepId: "notify",
    effect: token => notificationClient.SendAsync(payload, token),
    cancellationToken: ct);
```

If the effect already completed successfully during an earlier replay, `CaptureAsync` bypasses the delegate and returns the persisted `Result<T>` along with any metadata.

## Error behaviour

- Missing branches or unsupported versions surface `error.version.conflict` with metadata containing `changeId`, `version`, `minVersion`, and `maxVersion`.
- Exceptions thrown inside a branch are converted to `error.exception` via `Error.FromException`.
- `OperationCanceledException` becomes `error.canceled`, preserving the triggering token when available.

Use these codes to build observability dashboards or to drive automated replay diagnostics.

## Observability

- Instrument `GoDiagnostics` to emit `workflow.*` metrics and activity tags. Replay counts flow through the `workflow.replay.count` histogram while logical clock increments surface under `workflow.logical.clock`.
- `DeterministicGate` and `DeterministicEffectStore` emit `Deterministic.*` activities (for example `version_gate.require`, `workflow.execute`, `effect.capture`) so OmniRelay/SHOW HISTORY views can correlate commits with queue events.
- When a deterministic branch fails, propagate `Result<T>.Error.Metadata` into logs or tracing scopes. Keys include `changeId`, `version`, `minVersion`, `maxVersion`, and the scoped `stepId` from `DeterministicWorkflowContext.CreateEffectId`.
- Attach `Result<T>.Error.Metadata` to structured logs so OTLP/Prometheus pipelines can slice failures by change/version. For example, enrich Serilog scopes with `@error.Metadata` and configure OpenTelemetry resource attributes from the same payload.
- Combine `DeterministicWorkflowContext.Metadata` with `WorkflowExecutionContext` to correlate deterministic steps with workflow executions. The latter already exports tags like `workflow.namespace`, `workflow.id`, and `workflow.logical_clock` for activity traces.
