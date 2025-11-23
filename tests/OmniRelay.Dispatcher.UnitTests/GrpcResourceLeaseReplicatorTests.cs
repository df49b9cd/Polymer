using Hugo;
using OmniRelay.Dispatcher.Grpc;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class GrpcResourceLeaseReplicatorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask PublishAsync_InvokesGrpcAndSinks()
    {
        var client = new RecordingClient();
        var sink = new RecordingSink();
        var replicator = new GrpcResourceLeaseReplicator(client, sinks: [sink]);

        var result = await replicator.PublishAsync(CreateEvent(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(client.Requests);
        Assert.Equal(1, client.Requests[0].SequenceNumber);
        Assert.Single(sink.Events);
        Assert.Equal(1, sink.Events[0].SequenceNumber);
    }

    private static ResourceLeaseReplicationEvent CreateEvent() =>
        new(
            0,
            ResourceLeaseReplicationEventType.Heartbeat,
            DateTimeOffset.UtcNow,
            null,
            "peer-a",
            new ResourceLeaseItemPayload("resource", "id", "pk", "json", []),
            new ResourceLeaseErrorInfo("info", "code"),
            []);

    private sealed class RecordingClient : IGrpcResourceLeaseReplicatorClient
    {
        public List<ResourceLeaseReplicationEventMessage> Requests { get; } = [];

        public async ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEventMessage message, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Requests.Add(message);
            return Ok(Unit.Value);
        }
    }

    private sealed class RecordingSink : IResourceLeaseReplicationSink
    {
        public List<ResourceLeaseReplicationEvent> Events { get; } = [];

        public ValueTask<Result<Unit>> ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            Events.Add(replicationEvent);
            return ValueTask.FromResult(Ok(Unit.Value));
        }
    }
}
