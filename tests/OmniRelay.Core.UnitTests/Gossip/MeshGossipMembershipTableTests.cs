using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using OmniRelay.Core.Gossip;
using Xunit;

namespace OmniRelay.Core.UnitTests.Gossip;

public sealed class MeshGossipMembershipTableTests
{
    [Fact]
    public void MarkObserved_AddsPeerAndUpgradesMetadata()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var localMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "local",
            Role = "dispatcher",
            ClusterId = "cluster-a",
            Region = "local",
            MeshVersion = "dev",
            MetadataVersion = 1
        };

        var table = new MeshGossipMembershipTable(localMetadata.NodeId, localMetadata, time);

        var remoteMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "peer-a",
            Role = "worker",
            ClusterId = "cluster-a",
            Region = "region-a",
            MeshVersion = "1.0.0",
            MetadataVersion = 1,
            Labels = ImmutableDictionary<string, string>.Empty.Add("mesh.zone", "az-1")
        };

        time.Advance(TimeSpan.FromSeconds(1));
        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = remoteMetadata.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = time.GetUtcNow(),
            Metadata = remoteMetadata
        });

        var snapshot = table.Snapshot();
        var peer = snapshot.Members.Where(member => member.NodeId == remoteMetadata.NodeId).ShouldHaveSingleItem();
        peer.Metadata.Role.ShouldBe("worker");
        peer.Metadata.Labels["mesh.zone"].ShouldBe("az-1");

        var upgraded = remoteMetadata with
        {
            MeshVersion = "1.1.0",
            MetadataVersion = remoteMetadata.MetadataVersion + 1,
            Labels = remoteMetadata.Labels
                .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
                .SetItem("mesh.zone", "az-2")
        };

        time.Advance(TimeSpan.FromSeconds(1));
        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = upgraded.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = time.GetUtcNow(),
            Metadata = upgraded
        });

        snapshot = table.Snapshot();
        peer = snapshot.Members.Where(member => member.NodeId == upgraded.NodeId).ShouldHaveSingleItem();
        peer.Metadata.MeshVersion.ShouldBe("1.1.0");
        peer.Metadata.Labels["mesh.zone"].ShouldBe("az-2");
    }

    [Fact]
    public void Sweep_MarksPeersSuspectThenLeft()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var localMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "local",
            Role = "dispatcher",
            ClusterId = "cluster-a",
            Region = "local",
            MeshVersion = "dev",
            MetadataVersion = 1
        };

        var table = new MeshGossipMembershipTable(localMetadata.NodeId, localMetadata, time);
        var remoteMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "peer-a",
            Role = "worker",
            ClusterId = "cluster-a",
            Region = "region-a",
            MeshVersion = "1.0.0",
            MetadataVersion = 1
        };

        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = remoteMetadata.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = time.GetUtcNow(),
            Metadata = remoteMetadata
        });

        time.Advance(TimeSpan.FromSeconds(6));
        table.Sweep(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12));
        var snapshot = table.Snapshot();
        var peer = snapshot.Members.Where(member => member.NodeId == remoteMetadata.NodeId).ShouldHaveSingleItem();
        peer.Status.ShouldBe(MeshGossipMemberStatus.Suspect);

        time.Advance(TimeSpan.FromSeconds(7));
        table.Sweep(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12));
        snapshot = table.Snapshot();
        peer = snapshot.Members.Where(member => member.NodeId == remoteMetadata.NodeId).ShouldHaveSingleItem();
        peer.Status.ShouldBe(MeshGossipMemberStatus.Left);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _current;

        public TestTimeProvider(DateTimeOffset start) => _current = start;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan delta) => _current += delta;
    }

    [Fact]
    public void PickFanout_ReturnsRandomSubset()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var localMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "local",
            Role = "dispatcher",
            ClusterId = "cluster-a",
            Region = "local",
            MeshVersion = "dev",
            MetadataVersion = 1
        };

        var table = new MeshGossipMembershipTable(localMetadata.NodeId, localMetadata, time);

        for (int i = 0; i < 10; i++)
        {
            var metadata = new MeshGossipMemberMetadata
            {
                NodeId = $"peer-{i}",
                Role = "worker",
                ClusterId = "cluster-a",
                Region = "region-a",
                MeshVersion = "1.0.0",
                MetadataVersion = 1
            };

            table.MarkObserved(new MeshGossipMemberSnapshot
            {
                NodeId = metadata.NodeId,
                Status = MeshGossipMemberStatus.Alive,
                LastSeen = time.GetUtcNow(),
                Metadata = metadata
            });
        }

        var fanout = table.PickFanout(3);
        fanout.Count.ShouldBe(3);
    }

    [Fact]
    public void Snapshot_IncludesLocalMember()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var localMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "local",
            Role = "dispatcher",
            ClusterId = "cluster-a",
            Region = "local",
            MeshVersion = "dev",
            MetadataVersion = 1
        };

        var table = new MeshGossipMembershipTable(localMetadata.NodeId, localMetadata, time);
        var snapshot = table.Snapshot();

        snapshot.Members.ShouldNotBeEmpty();
        var localMember = snapshot.Members.FirstOrDefault(m => m.NodeId == "local");
        localMember.ShouldNotBeNull();
        localMember!.Status.ShouldBe(MeshGossipMemberStatus.Alive);
    }

    [Fact]
    public void MarkSender_UpdatesHeartbeat()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var localMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "local",
            Role = "dispatcher",
            ClusterId = "cluster-a",
            Region = "local",
            MeshVersion = "dev",
            MetadataVersion = 1
        };

        var table = new MeshGossipMembershipTable(localMetadata.NodeId, localMetadata, time);

        var senderMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "sender",
            Role = "worker",
            ClusterId = "cluster-a",
            Region = "region-a",
            MeshVersion = "1.0.0",
            MetadataVersion = 1
        };

        var envelope = new MeshGossipEnvelope
        {
            SchemaVersion = MeshGossipOptions.CurrentSchemaVersion,
            Sender = senderMetadata,
            Members = Array.Empty<MeshGossipMemberSnapshot>(),
            Sequence = 1
        };

        table.MarkSender(envelope, 10.0);
        var snapshot = table.Snapshot();

        var sender = snapshot.Members.FirstOrDefault(m => m.NodeId == "sender");
        sender.ShouldNotBeNull();
        sender!.Status.ShouldBe(MeshGossipMemberStatus.Alive);
    }
}
