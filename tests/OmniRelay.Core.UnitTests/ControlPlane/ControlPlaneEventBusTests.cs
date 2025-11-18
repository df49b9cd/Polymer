using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Events;
using OmniRelay.Core.Gossip;
using Xunit;

namespace OmniRelay.Core.UnitTests.ControlPlane;

public sealed class ControlPlaneEventBusTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PublishAsync_DeliversEventsToSubscribers()
    {
        var bus = new ControlPlaneEventBus(NullLogger<ControlPlaneEventBus>.Instance);
        await using var subscription = bus.Subscribe();

        var evt = CreateMembershipEvent(clusterId: "cluster-a");
        var publish = await bus.PublishAsync(evt, TestContext.Current.CancellationToken);
        publish.IsSuccess.ShouldBeTrue(publish.Error?.Message);

        var delivered = await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken);
        delivered.ShouldBe(evt);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PublishAsync_AppliesFilters()
    {
        var bus = new ControlPlaneEventBus(NullLogger<ControlPlaneEventBus>.Instance);
        await using var subscription = bus.Subscribe(new ControlPlaneEventFilter { ClusterId = "cluster-a" });

        var otherClusterEvent = CreateMembershipEvent(clusterId: "cluster-b");
        var ignored = await bus.PublishAsync(otherClusterEvent, TestContext.Current.CancellationToken);
        ignored.IsSuccess.ShouldBeTrue();
        subscription.Reader.TryRead(out _).ShouldBeFalse();

        var matching = CreateMembershipEvent(clusterId: "cluster-a");
        var deliveredResult = await bus.PublishAsync(matching, TestContext.Current.CancellationToken);
        deliveredResult.IsSuccess.ShouldBeTrue();
        var delivered = await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken);
        delivered.ShouldBe(matching);
    }

    private static GossipMembershipEvent CreateMembershipEvent(string clusterId)
    {
        var metadata = new MeshGossipMemberMetadata
        {
            NodeId = "node-a",
            ClusterId = clusterId,
            Role = "dispatcher",
            Region = "us-west",
            MeshVersion = "test",
            Endpoint = "https://node-a:17421"
        };

        var snapshot = new MeshGossipClusterView(
            DateTimeOffset.UtcNow,
            ImmutableArray.Create(new MeshGossipMemberSnapshot
            {
                NodeId = metadata.NodeId,
                Status = MeshGossipMemberStatus.Alive,
                LastSeen = DateTimeOffset.UtcNow,
                Metadata = metadata
            }),
            metadata.NodeId,
            MeshGossipOptions.CurrentSchemaVersion);

        return new GossipMembershipEvent(
            metadata.ClusterId,
            metadata.Role,
            metadata.Region,
            "test",
            snapshot,
            snapshot.Members[0]);
    }
}
