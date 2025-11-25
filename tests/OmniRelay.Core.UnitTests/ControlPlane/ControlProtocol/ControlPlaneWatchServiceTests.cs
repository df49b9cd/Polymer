using System.Threading.Tasks;
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
            ["core/v1"],
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

        response.Version.ShouldBe("v1");
        response.Epoch.ShouldBe(1);
        response.RequiredCapabilities.ShouldContain("core/v1");
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

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Watch_ReturnsError_WhenMissingRequiredCapabilities()
    {
        var options = Options.Create(new ControlProtocolOptions
        {
            UnsupportedCapabilityBackoff = TimeSpan.FromSeconds(7)
        });
        var updateStream = new ControlPlaneUpdateStream(options, NullLogger<ControlPlaneUpdateStream>.Instance);
        await updateStream.PublishAsync(new ControlPlaneUpdate(
            "v2",
            2,
            "demo"u8.ToArray(),
            ["dsl/v1"],
            true,
            ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        var service = new ControlPlaneWatchService(updateStream, options, NullLogger<ControlPlaneWatchService>.Instance);
        var writer = new RecordingStreamWriter<ControlWatchResponse>();

        await service.Watch(new ControlWatchRequest
        {
            NodeId = "node-a",
            Capabilities = new CapabilitySet { Items = { "core/v1" } }
        }, writer, new TestServerCallContext(CancellationToken.None));

        writer.Messages.ShouldHaveSingleItem();
        var response = writer.Messages[0];
        response.Error.ShouldNotBeNull();
        response.Error.Code.ShouldBe(ControlProtocolErrors.UnsupportedCapabilityCode);
        response.Backoff.Millis.ShouldBe(7000);
        response.RequiredCapabilities.ShouldContain("dsl/v1");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Watch_SetsFullSnapshot_WhenResumeTokenMismatch()
    {
        var options = Options.Create(new ControlProtocolOptions());
        var updateStream = new ControlPlaneUpdateStream(options, NullLogger<ControlPlaneUpdateStream>.Instance);
        await updateStream.PublishAsync(new ControlPlaneUpdate(
            "v3",
            3,
            "demo"u8.ToArray(),
            ["core/v1"],
            true,
            ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        var service = new ControlPlaneWatchService(updateStream, options, NullLogger<ControlPlaneWatchService>.Instance);
        var writer = new RecordingStreamWriter<ControlWatchResponse>();
        using var cts = new CancellationTokenSource();
        var context = new TestServerCallContext(cts.Token);
        var watchTask = service.Watch(new ControlWatchRequest
        {
            NodeId = "node-a",
            Capabilities = new CapabilitySet { Items = { "core/v1" } },
            ResumeToken = new WatchResumeToken { Version = "old", Epoch = 1 }
        }, writer, context);

        await writer.WaitForFirstAsync(TimeSpan.FromSeconds(1));
        cts.Cancel();
        await watchTask;

        writer.Messages.ShouldHaveSingleItem();

        var response = writer.Messages[0];
        response.FullSnapshot.ShouldBeTrue();
        response.Version.ShouldBe("v3");
        response.Epoch.ShouldBe(3);
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

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}

internal sealed class RecordingStreamWriter<T> : IServerStreamWriter<T>
{
    private readonly List<T> _messages = new();
    private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<T> Messages => _messages;

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message)
    {
        _messages.Add(message);
        _firstWrite.TrySetResult();
        return Task.CompletedTask;
    }

    public Task WaitForFirstAsync(TimeSpan? timeout = null)
    {
        if (timeout is null)
        {
            return _firstWrite.Task;
        }

        return _firstWrite.Task.WaitAsync(timeout.Value);
    }
}
