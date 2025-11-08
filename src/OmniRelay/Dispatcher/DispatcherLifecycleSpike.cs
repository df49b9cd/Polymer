using System.Threading.Channels;
using Hugo;
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
        var readiness = Go.MakeChannel<string>();
        var wg = new WaitGroup();

        foreach (var (step, index) in startSteps.Select((step, index) => (step, index)))
        {
            wg.Go(async token =>
            {
                await step(token).ConfigureAwait(false);
                await readiness.Writer.WriteAsync($"start:{index}", token).ConfigureAwait(false);
            }, cancellationToken);
        }

        await wg.WaitAsync(cancellationToken).ConfigureAwait(false);
        readiness.Writer.TryComplete();

        await foreach (var item in readiness.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            started.Add(item);
        }

        foreach (var (step, index) in stopSteps.Select((step, index) => (step, index)))
        {
            await step(cancellationToken).ConfigureAwait(false);
            stopped.Add($"stop:{index}");
        }

        return (started, stopped);
    }
}
