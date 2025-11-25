using Hugo;
using Hugo.Policies;

namespace OmniRelay.ControlPlane.Primitives;

/// <summary>Lightweight delay helper that routes through Hugo ResultPipelineTimers for AOT-safe, exception-free delays.</summary>
internal static class AsyncDelay
{
    public static ValueTask<Result<Go.Unit>> DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        DelayAsync(delay, TimeProvider.System, cancellationToken);

    public static ValueTask<Result<Go.Unit>> DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(Result.Ok(Go.Unit.Value));
        }

        return Result.RetryWithPolicyAsync<Go.Unit>(
            (ctx, ct) => ResultPipelineTimers.DelayAsync(ctx, delay, ct),
            ResultExecutionPolicy.None,
            timeProvider,
            cancellationToken);
    }
}
