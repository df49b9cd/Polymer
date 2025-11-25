using AwesomeAssertions;
using Hugo;
using NSubstitute;
using Xunit;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

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

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ShardedReplicator_AppendsShardMetadata()
    {
        var inner = Substitute.For<IResourceLeaseReplicator>();
        inner.PublishAsync(Arg.Any<ResourceLeaseReplicationEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.FromResult(Ok(Unit.Value)));

        var replicator = new ShardedResourceLeaseReplicator(inner, "users");
        var evt = SampleEvent();

        var result = await replicator.PublishAsync(evt, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error?.ToString());
        await inner.Received(1).PublishAsync(
            Arg.Is<ResourceLeaseReplicationEvent>(e => HasShard(e, "users")),
            CancellationToken.None);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompositeReplicator_FansOut()
    {
        var first = Substitute.For<IResourceLeaseReplicator>();
        var second = Substitute.For<IResourceLeaseReplicator>();
        first.PublishAsync(Arg.Any<ResourceLeaseReplicationEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.FromResult(Ok(Unit.Value)));
        second.PublishAsync(Arg.Any<ResourceLeaseReplicationEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.FromResult(Ok(Unit.Value)));

        var composite = new CompositeResourceLeaseReplicator([first, second]);
        var evt = SampleEvent();

        var result = await composite.PublishAsync(evt, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error?.ToString());
        await first.Received(1).PublishAsync(evt, Arg.Any<CancellationToken>());
        await second.Received(1).PublishAsync(evt, Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompositeReplicator_PropagatesFailureWithMetadata()
    {
        var failing = Substitute.For<IResourceLeaseReplicator>();
        failing.PublishAsync(Arg.Any<ResourceLeaseReplicationEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.FromResult(Err<Unit>(Error.From("boom", "test.failure"))));

        var other = Substitute.For<IResourceLeaseReplicator>();
        other.PublishAsync(Arg.Any<ResourceLeaseReplicationEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.FromResult(Ok(Unit.Value)));

        var composite = new CompositeResourceLeaseReplicator([failing, other]);

        var result = await composite.PublishAsync(SampleEvent(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Metadata.Should().ContainKey("replication.stage");
        result.Error!.Metadata["replication.stage"].Should().Be("composite.publish");
        result.Error!.Metadata.Should().ContainKey("replication.replicator");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompositeReplicator_CancellationSurfacesAsErrorCanceled()
    {
        var first = Substitute.For<IResourceLeaseReplicator>();
        first.PublishAsync(Arg.Any<ResourceLeaseReplicationEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var token = callInfo.Arg<CancellationToken>();
                return ValueTask.FromResult(Err<Unit>(Error.Canceled(token: token)));
            });

        var composite = new CompositeResourceLeaseReplicator([first]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await composite.PublishAsync(SampleEvent(), cts.Token);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ErrorCodes.Canceled);
    }
}
