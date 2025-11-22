using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Experimental helper to run start/stop steps and observe their completion order.
/// </summary>
public static class DispatcherLifecycleSpike
{
    /// <summary>
    /// Runs start steps concurrently, reports their completion order, then runs stop steps sequentially.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Started, IReadOnlyList<string> Stopped)> RunAsync(
        IReadOnlyList<Func<CancellationToken, Task>> startSteps,
        IReadOnlyList<Func<CancellationToken, Task>> stopSteps,
        CancellationToken cancellationToken)
    {
        var started = new List<string>();
        var stopped = new List<string>();
        var readiness = MakeChannel<string>();

        using (var group = new ErrGroup(cancellationToken))
        {
            foreach (var (step, index) in startSteps.Select((step, index) => (step, index)))
            {
                var label = $"start:{index}";
                group.Go(async token =>
                {
                    await step(token).ConfigureAwait(false);
                    await readiness.Writer.WriteAsync(label, token).ConfigureAwait(false);
                });
            }

            var waitResult = await group.WaitAsync(cancellationToken).ConfigureAwait(false);
            readiness.Writer.TryComplete();

            await foreach (var item in readiness.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                started.Add(item);
            }

            if (waitResult.IsFailure && waitResult.Error is { } error)
            {
                throw OmniRelayErrors.FromError(error, "dispatcher-lifecycle-spike");
            }
        }

        foreach (var (step, index) in stopSteps.Select((step, index) => (step, index)))
        {
            await step(cancellationToken).ConfigureAwait(false);
            stopped.Add($"stop:{index}");
        }

        return (started, stopped);
    }
}
