using System;
using System.IO;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OmniRelay.ControlPlane.Agent;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Protos.Control;
using Xunit;

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

            var client = new FakeWatchClient(new[] { response });
            var validator = Substitute.For<IControlPlaneConfigValidator>();
            validator.Validate(Arg.Any<byte[]>(), out Arg.Any<string?>()).Returns(callInfo => { callInfo[1] = null; return true; });
            var applier = Substitute.For<IControlPlaneConfigApplier>();
            var cache = new LkgCache(tempPath);
            var telemetry = new TelemetryForwarder(NullLogger<TelemetryForwarder>.Instance);
            var harness = new WatchHarness(client, validator, applier, cache, telemetry, NullLogger<WatchHarness>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var result = await harness.RunAsync(new ControlWatchRequest { NodeId = "node-a" }, cts.Token);

            Assert.True(result.IsSuccess);
            await applier.Received(1).ApplyAsync("v42", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());

            var lkg = await cache.TryLoadAsync(TestContext.Current.CancellationToken);
            Assert.True(lkg.IsSuccess);
            Assert.NotNull(lkg.Value);
            Assert.Equal("v42", lkg.Value!.Version);
            Assert.Equal(7, lkg.Value.Epoch);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}

internal sealed class FakeWatchClient : IControlPlaneWatchClient
{
    private readonly IEnumerable<ControlWatchResponse> _responses;

    public FakeWatchClient(IEnumerable<ControlWatchResponse> responses)
    {
        _responses = responses;
    }

    public IAsyncEnumerable<ControlWatchResponse> WatchAsync(ControlWatchRequest request, CancellationToken cancellationToken = default)
    {
        return _responses.ToAsyncEnumerable();
    }

    public Task<ControlSnapshotResponse> SnapshotAsync(ControlSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ControlSnapshotResponse());
    }
}
