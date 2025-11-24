using AwesomeAssertions;
using Hugo;
using OmniRelay.Dispatcher;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Dispatcher;

public class DispatcherLifecycleSpikeTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RunAsync_CoordinatesStartAndStopSequences()
    {
        var startSteps = new List<Func<CancellationToken, ValueTask<Result<Unit>>>>
        {
            async ct =>
            {
                await Task.Delay(50, ct);
                return Ok(Unit.Value);
            },
            async ct =>
            {
                await Task.Delay(10, ct);
                return Ok(Unit.Value);
            }
        };

        var stopSteps = new List<Func<CancellationToken, ValueTask<Result<Unit>>>>
        {
            async ct =>
            {
                await Task.Delay(5, ct);
                return Ok(Unit.Value);
            },
            async ct =>
            {
                await Task.Delay(15, ct);
                return Ok(Unit.Value);
            }
        };

        var result = await DispatcherLifecycleSpike.RunAsync(
            startSteps,
            stopSteps,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        var (started, stopped) = result.Value;

        started.Should().HaveCount(startSteps.Count);
        Enumerable.Range(0, startSteps.Count).Should().AllSatisfy(index =>
            started.Should().Contain($"start:{index}"));

        stopped.Should().HaveCount(stopSteps.Count);
        stopped.Should().Equal("stop:0", "stop:1");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RunAsync_PropagatesCancellation()
    {
        var startSteps = new List<Func<CancellationToken, ValueTask<Result<Unit>>>>
        {
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(Unit.Value);
            }
        };

        var stopSteps = new List<Func<CancellationToken, ValueTask<Result<Unit>>>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await DispatcherLifecycleSpike.RunAsync(startSteps, stopSteps, cts.Token);

        result.IsFailure.Should().BeTrue();
        result.Error?.Code.Should().Be(Error.Canceled().Code);
    }
}
