using System;
using System.Threading;
using Hugo;
using Hugo.Policies;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.ControlPlane.Primitives;

/// <summary>Lightweight delay helper that routes through Hugo ResultPipelineTimers for AOT-safe, exception-free delays.</summary>
internal static class AsyncDelay
{
    public static ValueTask<Result<Unit>> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(Ok(Unit.Value));
        }

        var context = new ResultPipelineStepContext("delay", new CompensationScope(), TimeProvider.System, cancellationToken);
        return ResultPipelineTimers.DelayAsync(context, delay, cancellationToken);
    }
}
