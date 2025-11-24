using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Protos.Control;
using Xunit;

namespace OmniRelay.Core.UnitTests.ControlPlane.ControlProtocol;

public sealed class ControlPlaneWatchServiceTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Snapshot_ReturnsPayload_WhenCapabilitiesSupported()
    {
        var options = Options.Create(new ControlProtocolOptions());
        var updateStream = new ControlPlaneUpdateStream(options, NullLogger<ControlPlaneUpdateStream>.Instance);
        await updateStream.PublishAsync(new ControlPlaneUpdate(
            "v1",
            1,
            "demo"u8.ToArray(),
            new[] { "core/v1" },
            true,
            ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        var service = new ControlPlaneWatchService(updateStream, options, NullLogger<ControlPlaneWatchService>.Instance);
        var request = new ControlWatchRequest
        {
            NodeId = "node-a",
            Capabilities = new CapabilitySet { Items = { "core/v1" } }
        };

        var response = await service.Snapshot(new ControlSnapshotRequest
        {
            Capabilities = request.Capabilities,
            NodeId = request.NodeId
        }, new TestServerCallContext(CancellationToken.None));

        Assert.Equal("v1", response.Version);
        Assert.Equal(1, response.Epoch);
        Assert.Contains("core/v1", response.RequiredCapabilities);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Snapshot_Throws_WhenCapabilityUnsupported()
    {
        var options = Options.Create(new ControlProtocolOptions());
        var updateStream = new ControlPlaneUpdateStream(options, NullLogger<ControlPlaneUpdateStream>.Instance);
        await updateStream.PublishAsync(ControlPlaneUpdate.Empty, TestContext.Current.CancellationToken);

        var service = new ControlPlaneWatchService(updateStream, options, NullLogger<ControlPlaneWatchService>.Instance);
        var request = new ControlWatchRequest
        {
            NodeId = "node-a",
            Capabilities = new CapabilitySet { Items = { "core/v9" } }
        };

        await Assert.ThrowsAsync<RpcException>(async () =>
            await service.Snapshot(new ControlSnapshotRequest
            {
                Capabilities = request.Capabilities,
                NodeId = request.NodeId
            }, new TestServerCallContext(CancellationToken.None)));
    }
}

internal sealed class TestServerCallContext : ServerCallContext
{
    private readonly CancellationToken _cancellationToken;

    public TestServerCallContext(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
    }

    protected override string MethodCore => "test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "peer";
    protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
    protected override Metadata RequestHeadersCore { get; } = new();
    protected override CancellationToken CancellationTokenCore => _cancellationToken;
    protected override Metadata ResponseTrailersCore { get; } = new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore { get; } = new(string.Empty, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) => throw new NotImplementedException();
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
