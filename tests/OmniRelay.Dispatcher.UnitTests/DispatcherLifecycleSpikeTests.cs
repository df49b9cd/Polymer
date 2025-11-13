using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherLifecycleSpikeTests
{
    [Fact]
    public async Task RunAsync_ReportsStartAndStopOrder()
    {
        var startSteps = new List<Func<CancellationToken, Task>>
        {
            async token => await Task.Delay(10, token),
            async token => await Task.Delay(1, token)
        };

        var stopSteps = new List<Func<CancellationToken, Task>>
        {
            token => Task.CompletedTask,
            token => Task.CompletedTask
        };

        var (started, stopped) = await DispatcherLifecycleSpike.RunAsync(startSteps, stopSteps, CancellationToken.None);

        Assert.Contains("start:0", started);
        Assert.Contains("start:1", started);
        Assert.Equal(["stop:0", "stop:1"], stopped);
    }
}
