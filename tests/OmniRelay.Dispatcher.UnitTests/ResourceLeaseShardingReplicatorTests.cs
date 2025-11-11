using NSubstitute;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class ResourceLeaseShardingReplicatorTests
{
    private static ResourceLeaseReplicationEvent SampleEvent() =>
        ResourceLeaseReplicationEvent.Create(
            ResourceLeaseReplicationEventType.Enqueue,
            ownership: null,
            peerId: null,
            payload: null,
            error: null,
            metadata: null);

    private static bool HasShard(ResourceLeaseReplicationEvent replicationEvent, string shardId) =>
        replicationEvent.Metadata.TryGetValue("shard.id", out var value) && value == shardId;

    [Fact]
    public async Task ShardedReplicator_AppendsShardMetadata()
    {
        var inner = Substitute.For<IResourceLeaseReplicator>();
        var replicator = new ShardedResourceLeaseReplicator(inner, "users");
        var evt = SampleEvent();

        await replicator.PublishAsync(evt, CancellationToken.None);

        await inner.Received(1).PublishAsync(
            Arg.Is<ResourceLeaseReplicationEvent>(e => HasShard(e, "users")),
            CancellationToken.None);
    }

    [Fact]
    public async Task CompositeReplicator_FansOut()
    {
        var first = Substitute.For<IResourceLeaseReplicator>();
        var second = Substitute.For<IResourceLeaseReplicator>();

        var composite = new CompositeResourceLeaseReplicator([first, second]);
        var evt = SampleEvent();

        await composite.PublishAsync(evt, CancellationToken.None);

        await first.Received(1).PublishAsync(evt, CancellationToken.None);
        await second.Received(1).PublishAsync(evt, CancellationToken.None);
    }
}
