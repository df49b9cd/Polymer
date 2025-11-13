using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Tests.Dispatcher;

public class DispatcherLifecycleSpikeTests
{
    [Fact]
    public async Task RunAsync_CoordinatesStartAndStopSequences()
    {
        var startSteps = new List<Func<CancellationToken, Task>>
        {
            async ct => await Task.Delay(50, ct),
            async ct => await Task.Delay(10, ct)
        };

        var stopSteps = new List<Func<CancellationToken, Task>>
        {
            async ct => await Task.Delay(5, ct),
            async ct => await Task.Delay(15, ct)
        };

        var (started, stopped) = await DispatcherLifecycleSpike.RunAsync(
            startSteps,
            stopSteps,
            CancellationToken.None);

        Assert.Equal(startSteps.Count, started.Count);
        Assert.All(Enumerable.Range(0, startSteps.Count), index =>
            Assert.Contains($"start:{index}", started));

        Assert.Equal(stopSteps.Count, stopped.Count);
        Assert.Equal(["stop:0", "stop:1"], stopped);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation()
    {
        var startSteps = new List<Func<CancellationToken, Task>>
        {
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };

        var stopSteps = new List<Func<CancellationToken, Task>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await DispatcherLifecycleSpike.RunAsync(startSteps, stopSteps, cts.Token);
        });
    }
}
