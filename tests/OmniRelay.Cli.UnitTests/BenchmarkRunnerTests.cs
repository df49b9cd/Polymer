using OmniRelay.Cli.UnitTests.Infrastructure;
using OmniRelay.Core;

namespace OmniRelay.Cli.UnitTests;

public sealed class BenchmarkRunnerTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RunAsync_PerformsWarmup_AndHonorsMaxRequests()
    {
        var invocation = CreateInvocation();
        var fakeInvoker = new FakeBenchmarkInvoker(TimeSpan.FromMilliseconds(2));
        BenchmarkRunner.InvokerFactoryOverride = (_, _) => Task.FromResult<BenchmarkRunner.IRequestInvoker>(fakeInvoker);

        var options = new BenchmarkRunner.BenchmarkExecutionOptions(
            Concurrency: 3,
            MaxRequests: 5,
            Duration: null,
            RateLimitPerSecond: null,
            WarmupDuration: TimeSpan.FromMilliseconds(30),
            PerRequestTimeout: TimeSpan.FromSeconds(1));

        var summary = await BenchmarkRunner.RunAsync(invocation, options, CancellationToken.None);

        summary.Attempts.ShouldBe(5);
        summary.Successes.ShouldBe(5);
        ((long)fakeInvoker.CallCount).ShouldBeGreaterThan(summary.Attempts);
        fakeInvoker.MaxConcurrency.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RunAsync_StopsAfterDuration_WhenUnlimited()
    {
        var invocation = CreateInvocation();
        var fakeInvoker = new FakeBenchmarkInvoker(TimeSpan.FromMilliseconds(5));
        BenchmarkRunner.InvokerFactoryOverride = (_, _) => Task.FromResult<BenchmarkRunner.IRequestInvoker>(fakeInvoker);

        var duration = TimeSpan.FromMilliseconds(80);
        var options = new BenchmarkRunner.BenchmarkExecutionOptions(
            Concurrency: 2,
            MaxRequests: null,
            Duration: duration,
            RateLimitPerSecond: null,
            WarmupDuration: null,
            PerRequestTimeout: TimeSpan.FromSeconds(1));

        var summary = await BenchmarkRunner.RunAsync(invocation, options, CancellationToken.None);

        summary.Attempts.ShouldBeGreaterThan(0);
        summary.Successes.ShouldBe(summary.Attempts);
        summary.Elapsed.ShouldBeGreaterThanOrEqualTo(duration);
        ((long)fakeInvoker.CallCount).ShouldBe(summary.Attempts);
        fakeInvoker.MaxConcurrency.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RunAsync_RespectsRateLimit()
    {
        var invocation = CreateInvocation();
        var fakeInvoker = new FakeBenchmarkInvoker();
        BenchmarkRunner.InvokerFactoryOverride = (_, _) => Task.FromResult<BenchmarkRunner.IRequestInvoker>(fakeInvoker);

        var duration = TimeSpan.FromMilliseconds(400);
        var rateLimit = 5d;
        var options = new BenchmarkRunner.BenchmarkExecutionOptions(
            Concurrency: 4,
            MaxRequests: null,
            Duration: duration,
            RateLimitPerSecond: rateLimit,
            WarmupDuration: null,
            PerRequestTimeout: TimeSpan.FromSeconds(1));

        var summary = await BenchmarkRunner.RunAsync(invocation, options, CancellationToken.None);

        summary.Attempts.ShouldBeGreaterThan(1);
        summary.RequestsPerSecond.ShouldBeLessThanOrEqualTo(rateLimit + 1);
        var orderedTimes = fakeInvoker.CallTimestamps.OrderBy(static t => t).ToArray();
        orderedTimes.Length.ShouldBe((int)summary.Attempts);
        if (orderedTimes.Length > 1)
        {
            var expectedSpacing = TimeSpan.FromSeconds(1 / rateLimit);
            for (var index = 1; index < orderedTimes.Length; index++)
            {
                var delta = orderedTimes[index] - orderedTimes[index - 1];
                delta.ShouldBeGreaterThan(expectedSpacing - TimeSpan.FromMilliseconds(50));
            }
        }
        fakeInvoker.MaxConcurrency.ShouldBeLessThanOrEqualTo(4);
    }

    private static RequestInvocation CreateInvocation()
    {
        var meta = new RequestMeta(service: "benchmark", procedure: "demo::call");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        return new RequestInvocation(
            Transport: "http",
            Request: request,
            Timeout: TimeSpan.FromSeconds(5),
            HttpUrl: "https://localhost:8443",
            Addresses: Array.Empty<string>(),
            HttpClientRuntime: null,
            GrpcClientRuntime: null);
    }
}
