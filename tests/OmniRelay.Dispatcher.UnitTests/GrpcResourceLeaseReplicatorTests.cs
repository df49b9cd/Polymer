using OmniRelay.Dispatcher.Grpc;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class GrpcResourceLeaseReplicatorTests
{
    [Fact]
    public async Task PublishAsync_InvokesGrpcAndSinks()
    {
        var client = new RecordingClient();
        var sink = new RecordingSink();
        var replicator = new GrpcResourceLeaseReplicator(client, sinks: [sink]);

        await replicator.PublishAsync(CreateEvent(), TestContext.Current.CancellationToken);

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

        public async Task PublishAsync(ResourceLeaseReplicationEventMessage message, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Requests.Add(message);
        }
    }

    private sealed class RecordingSink : IResourceLeaseReplicationSink
    {
        public List<ResourceLeaseReplicationEvent> Events { get; } = [];

        public ValueTask ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            Events.Add(replicationEvent);
            return ValueTask.CompletedTask;
        }
    }
}
