using System;
using System.IO;
using Google.Protobuf;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OmniRelay.ControlPlane.Agent;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Protos.Control;
using Xunit;
using Microsoft.AspNetCore.Routing;

namespace OmniRelay.Core.UnitTests.ControlPlane.Agent;

public sealed class WatchHarnessTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RunAsync_AppliesUpdate_AndPersistsLkg()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"lkg-{Guid.NewGuid():N}.json");
        try
        {
            var payload = "hello"u8.ToArray();
            var response = new ControlWatchResponse
            {
                Version = "v42",
                Epoch = 7,
                Payload = Google.Protobuf.ByteString.CopyFrom(payload),
                ResumeToken = new WatchResumeToken { Version = "v42", Epoch = 7 },
                Backoff = new ControlBackoff { Millis = 1000 }
            };

        // Slow the stream very slightly so the apply pump processes before the assertion.
        var client = new FakeWatchClient([response], delayBetween: TimeSpan.FromMilliseconds(5));
            var validator = Substitute.For<IControlPlaneConfigValidator>();
            validator.Validate(Arg.Any<byte[]>(), out Arg.Any<string?>()).Returns(callInfo => { callInfo[1] = null; return true; });
            var applier = Substitute.For<IControlPlaneConfigApplier>();
            var cache = new LkgCache(tempPath);
            var telemetry = new TelemetryForwarder(NullLogger<TelemetryForwarder>.Instance);
            var harness = new WatchHarness(client, validator, applier, cache, telemetry, NullLogger<WatchHarness>.Instance);

            // Use an uncanceled token to avoid racing the apply pump; overall test is still bounded by xUnit timeout.
            var result = await harness.RunAsync(new ControlWatchRequest { NodeId = "node-a" }, CancellationToken.None);

            result.IsSuccess.ShouldBeTrue();
            await applier.Received(1).ApplyAsync("v42", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());

            var lkg = await cache.TryLoadAsync(TestContext.Current.CancellationToken);
            lkg.IsSuccess.ShouldBeTrue();
            lkg.Value.ShouldNotBeNull();
            lkg.Value!.Version.ShouldBe("v42");
            lkg.Value.Epoch.ShouldBe(7);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Theory]
    [InlineData(0, 0, 1000)]
    [InlineData(3, 0, 8000)]
    [InlineData(5, 0, 30000)]
    [InlineData(2, 5000, 5000)]
    [InlineData(4, 40000, 30000)]
    public void ComputeBackoff_UsesServerHintOrExponential(int attempt, long serverBackoffMillis, int expectedMillis)
    {
        var backoff = WatchHarness.ComputeBackoff(attempt, serverBackoffMillis);
        ((int)backoff.TotalMilliseconds).ShouldBe(expectedMillis);
    }
}

internal sealed class FakeWatchClient : IControlPlaneWatchClient
{
    private readonly IEnumerable<ControlWatchResponse> _responses;
    private readonly TimeSpan _delayBetween;

    public FakeWatchClient(IEnumerable<ControlWatchResponse> responses, TimeSpan? delayBetween = null)
    {
        _responses = responses;
        _delayBetween = delayBetween ?? TimeSpan.Zero;
    }

    public IAsyncEnumerable<ControlWatchResponse> WatchAsync(ControlWatchRequest request, CancellationToken cancellationToken = default)
    {
        return Slow(_responses, _delayBetween, cancellationToken);
    }

    public Task<ControlSnapshotResponse> SnapshotAsync(ControlSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ControlSnapshotResponse());
    }

    private static async IAsyncEnumerable<ControlWatchResponse> Slow(
        IEnumerable<ControlWatchResponse> source,
        TimeSpan delay,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in source)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            yield return item;
        }
    }
}
