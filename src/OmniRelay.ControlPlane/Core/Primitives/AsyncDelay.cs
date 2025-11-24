using System;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
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

        return Result.TryAsync<Unit>(async ct =>
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return Unit.Value;
        }, cancellationToken: cancellationToken);
    }
}
