using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Events;
using OmniRelay.Core.Gossip;
using Shouldly;
using Xunit;

namespace OmniRelay.Core.UnitTests.ControlPlane;

public sealed class ControlPlaneEventBusTests
{
    [Fact]
    public async Task PublishAsync_DeliversEventsToSubscribers()
    {
        var bus = new ControlPlaneEventBus(NullLogger<ControlPlaneEventBus>.Instance);
        await using var subscription = bus.Subscribe();

        var evt = CreateMembershipEvent(clusterId: "cluster-a");
        await bus.PublishAsync(evt, TestContext.Current.CancellationToken);

        var delivered = await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken);
        delivered.ShouldBe(evt);
    }

    [Fact]
    public async Task PublishAsync_AppliesFilters()
    {
        var bus = new ControlPlaneEventBus(NullLogger<ControlPlaneEventBus>.Instance);
        await using var subscription = bus.Subscribe(new ControlPlaneEventFilter { ClusterId = "cluster-a" });

        var otherClusterEvent = CreateMembershipEvent(clusterId: "cluster-b");
        await bus.PublishAsync(otherClusterEvent, TestContext.Current.CancellationToken);
        subscription.Reader.TryRead(out _).ShouldBeFalse();

        var matching = CreateMembershipEvent(clusterId: "cluster-a");
        await bus.PublishAsync(matching, TestContext.Current.CancellationToken);
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
