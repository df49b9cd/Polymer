# WORK-024Q – SafeTaskQueue-Based Pumps (MeshKit Tutorial Alignment)

## Goal
Refactor existing pump loops to Hugo’s SafeTaskQueue + TaskQueueChannelAdapter pattern for bounded, durable, and AOT-safe message handling (per MeshKit tutorial “SafeTaskQueue Pumps for Outbound Messages”).

## Scope (code targets)
- Gossip outbound/inbound pumps in `src/OmniRelay.ControlPlane/Core/Gossip/MeshGossipHost.cs`.
- Control agent apply/watch pipelines in `src/OmniRelay.ControlPlane/Core/Agent/WatchHarness.cs` (inbound control updates, outbound apply tasks).
- Leadership coordination background tasks in `src/OmniRelay.ControlPlane/Core/LeadershipCoordinator.cs` (election/renew/observe pumps) where applicable.
- Dispatcher/data-plane pumps that shuttle frames/leases between channels (identify in `src/OmniRelay.Dispatcher/*` and streaming call types in `src/OmniRelay.DataPlane/Transport/Grpc/*` and `src/OmniRelay.DataPlane/Transport/Http/*`).

## Pattern to apply
```csharp
var queue = SafeTaskQueue<MeshMessage>.Create(new SafeTaskQueueOptions
{
    MaxRetries = 5,
    PoisonQueueName = "mesh-poison"
});

var adapter = TaskQueueChannelAdapter<MeshMessage>.Create(
    queue,
    concurrency: Environment.ProcessorCount,
    ownsQueue: false);

var ctx = _contextFactory.Create("mesh-outbound", cancellationToken);

await adapter.Reader
    .ReadAllAsync(cancellationToken)
    .Select(lease => Result.Ok(lease))
    .ForEachLinkedCancellationAsync(async (leaseResult, token) =>
    {
        if (leaseResult.IsFailure)
        {
            return leaseResult.CastFailure<Unit>();
        }

        var lease = leaseResult.Value;
        ctx.RegisterCompensation(_ => queue.FailAsync(lease, Error.From("rollback"), requeue: true, token));

        var sendResult = await SendMessageAsync(lease.Payload, ctx, token);
        if (sendResult.IsFailure)
        {
            await queue.FailAsync(lease, sendResult.Error!, requeue: !lease.IsPoisoned, token);
            return sendResult.CastFailure<Unit>();
        }

        await queue.AckAsync(lease, token);
        return Result.Ok(Unit.Value);
    },
    cancellationToken);
```

## Acceptance Criteria
- Identified pumps in Scope use `SafeTaskQueue<T>` + `TaskQueueChannelAdapter<T>` instead of ad-hoc channels/loops/retries.
- Concurrency, retry (`MaxRetries`), and poison handling are configurable per role (gossip/control/dispatcher) and documented.
- Compensations registered for side effects; failures call `FailAsync` with `requeue` semantics; successes `AckAsync`.
- No raw `Task.WhenAll/WhenAny` or manual `Task.Delay` remain inside pump loops; cancellations surface as `Error.Canceled`.
- Metrics/logging emit counts for ack/fail/poison and attempts.

## Status
Done

## Completion Notes
- Gossip outbound fan-out now runs on SafeTaskQueue + TaskQueueChannelAdapter in `MeshGossipHost`.
- Control watch apply path enqueues updates onto a SafeTaskQueue pump in `WatchHarness`.
- Leadership scope evaluations run through SafeTaskQueue in `LeadershipCoordinator`.
- Data-plane HTTP duplex request pump uses SafeTaskQueue/adapter to bound and structure frame handling in `HttpInbound`.
- Transport health watch moved to Result/Go delay helper to stay exception-free and pipeline-friendly.

## SLOs & CI gates
- Maintain or improve pump p99 latency; no unbounded buffering.
- CI: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj`, gossip/leadership tests; dispatcher/transport suites if pumps touch data-plane.
- MeshKit AOT smoke remains green (`tests/OmniRelay.MeshKit.AotSmoke`).

## Testing Strategy
- Unit: pump success, retry-then-success, poison after max retries, cancellation mid-pump (use virtual time where possible).
- Integration: gossip/control-plane/dispatcher smoke paths to confirm no message loss and graceful shutdown.
- Feature/Hyperscale: run if pump changes affect throughput/backpressure; document rationale otherwise.
