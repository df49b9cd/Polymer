using System.Collections.Immutable;
using System.Globalization;
using OmniRelay.Core.Gossip;
using Xunit;

namespace OmniRelay.Core.UnitTests.Gossip;

public sealed class MeshGossipMembershipTableTests
{
    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public void MarkObserved_DoesNotDowngradeLocalFromRemoteSnapshot()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var localMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "local-node",
            Role = "dispatcher",
            ClusterId = "cluster-a",
            Region = "local",
            MeshVersion = "1.0.0",
            MetadataVersion = 1
        };

        var table = new MeshGossipMembershipTable(localMetadata.NodeId, localMetadata, time);

        time.Advance(TimeSpan.FromSeconds(5));

        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = localMetadata.NodeId,
            Status = MeshGossipMemberStatus.Left,
            LastSeen = time.GetUtcNow().Subtract(TimeSpan.FromMinutes(10)),
            Metadata = localMetadata with
            {
                MeshVersion = "1.1.0",
                MetadataVersion = 5
            }
        });

        var snapshot = table.Snapshot();
        var local = snapshot.Members.First(member => member.NodeId == localMetadata.NodeId);
        local.Status.ShouldBe(MeshGossipMemberStatus.Alive);
        local.LastSeen.ShouldBe(time.GetUtcNow());
        local.Metadata.MeshVersion.ShouldBe("1.1.0");
        local.Metadata.MetadataVersion.ShouldBe(5);
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _current = start;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan delta) => _current += delta;
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public void PickFanout_SkipsLocalAndLeftAndAvoidsDuplicates()
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

        for (int i = 0; i < 20; i++)
        {
            var metadata = new MeshGossipMemberMetadata
            {
                NodeId = $"peer-{i:D2}",
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

        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = "peer-left",
            Status = MeshGossipMemberStatus.Left,
            LastSeen = time.GetUtcNow(),
            Metadata = new MeshGossipMemberMetadata
            {
                NodeId = "peer-left",
                Role = "worker",
                ClusterId = "cluster-a",
                Region = "region-a",
                MeshVersion = "1.0.0",
                MetadataVersion = 1
            }
        });

        var fanout = table.PickFanout(12);

        fanout.ShouldNotContain(member => member.NodeId == localMetadata.NodeId);
        fanout.ShouldNotContain(member => member.Status == MeshGossipMemberStatus.Left);
        fanout.Select(member => member.NodeId)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(fanout.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void PickFanout_AllocationsScaleWithFanout()
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

        for (int i = 0; i < 256; i++)
        {
            var metadata = new MeshGossipMemberMetadata
            {
                NodeId = $"peer-{i:D3}",
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

        // Warmup to avoid JIT noise.
        _ = table.PickFanout(8);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 8; i++)
        {
            var slice = table.PickFanout(8);
            slice.Count.ShouldBe(8);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var average = (after - before) / 8d;

        average.ShouldBeLessThanOrEqualTo(8_000d);
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public void Snapshot_OrdersLocalFirstThenOrdinal()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var localMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "node-c",
            Role = "dispatcher",
            ClusterId = "cluster-a",
            Region = "local",
            MeshVersion = "dev",
            MetadataVersion = 1
        };

        var table = new MeshGossipMembershipTable(localMetadata.NodeId, localMetadata, time);

        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = "node-a",
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = time.GetUtcNow(),
            Metadata = new MeshGossipMemberMetadata { NodeId = "node-a" }
        });

        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = "node-b",
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = time.GetUtcNow(),
            Metadata = new MeshGossipMemberMetadata { NodeId = "node-b" }
        });

        var snapshot = table.Snapshot();
        var nodeOrder = snapshot.Members.Select(m => m.NodeId).ToArray();

        nodeOrder.ShouldBe(["node-c", "node-a", "node-b"]);
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public void RefreshLocalMetadata_IncrementsVersionAndKeepsLocalAlive()
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
        time.Advance(TimeSpan.FromSeconds(10));

        var refreshed = table.RefreshLocalMetadata(metadata => metadata with
        {
            Role = "gateway",
            MeshVersion = "1.0.1"
        });

        refreshed.MetadataVersion.ShouldBe(2);

        var snapshot = table.Snapshot();
        var local = snapshot.Members.First(m => m.NodeId == "local");
        local.Metadata.MetadataVersion.ShouldBe(2);
        local.Metadata.Role.ShouldBe("gateway");
        local.Metadata.MeshVersion.ShouldBe("1.0.1");
        local.Status.ShouldBe(MeshGossipMemberStatus.Alive);
        local.LastSeen.ShouldBe(time.GetUtcNow());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void MarkSender_PreservesNewerMetadataAndUpdatesRoundTrip()
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

        var preferred = new MeshGossipMemberMetadata
        {
            NodeId = "peer",
            Role = "worker",
            ClusterId = "cluster-a",
            Region = "region-a",
            MeshVersion = "2.0.0",
            MetadataVersion = 5
        };

        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = preferred.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = time.GetUtcNow(),
            Metadata = preferred
        });

        var downgradedEnvelope = new MeshGossipEnvelope
        {
            SchemaVersion = MeshGossipOptions.CurrentSchemaVersion,
            Sender = preferred with
            {
                MetadataVersion = 2,
                MeshVersion = "legacy"
            },
            Members = Array.Empty<MeshGossipMemberSnapshot>(),
            Sequence = 2
        };

        table.MarkSender(downgradedEnvelope, 12.5);

        var snapshot = table.Snapshot();
        var peer = snapshot.Members.First(m => m.NodeId == "peer");
        peer.Metadata.MeshVersion.ShouldBe("2.0.0");
        peer.Metadata.MetadataVersion.ShouldBe(5);
        peer.RoundTripTimeMs.ShouldNotBeNull();
        peer.RoundTripTimeMs!.Value.ShouldBe(12.5, 0.01);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Sweep_MarksPeerLeftAfterLeaveInterval()
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
        var remote = new MeshGossipMemberMetadata
        {
            NodeId = "peer-left",
            Role = "worker",
            ClusterId = "cluster-a",
            Region = "region-a",
            MeshVersion = "1.0.0",
            MetadataVersion = 1
        };

        table.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = remote.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = time.GetUtcNow() - TimeSpan.FromSeconds(30),
            Metadata = remote
        });

        table.Sweep(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        var snapshot = table.Snapshot();
        snapshot.Members.ShouldContain(m => m.NodeId == "peer-left" && m.Status == MeshGossipMemberStatus.Left);
    }
}
