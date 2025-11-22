using System.Globalization;
using OmniRelay.Core.Shards.Hashing;
using Xunit;

namespace OmniRelay.Core.UnitTests.Shards.Hashing;

public sealed class ShardHashStrategyTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void RingStrategy_AssignsDeterministically()
    {
        var strategy = new RingShardHashStrategy();
        var request = CreateRequest("mesh.control", shardCount: 32);

        var first = strategy.Compute(request);
        var second = strategy.Compute(request);

        var mapA = first.Assignments.ToDictionary(x => x.ShardId, x => x.OwnerNodeId, StringComparer.OrdinalIgnoreCase);
        var mapB = second.Assignments.ToDictionary(x => x.ShardId, x => x.OwnerNodeId, StringComparer.OrdinalIgnoreCase);

        mapA.Count.ShouldBe(mapB.Count);
        foreach (var shard in mapA.Keys)
        {
            mapB.ShouldContainKey(shard);
            mapB[shard].ShouldBe(mapA[shard]);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RendezvousStrategy_RespectsWeights()
    {
        var strategy = new RendezvousShardHashStrategy();
        var request = new ShardHashRequest
        {
            Namespace = "mesh.telemetry",
            Nodes = new[]
            {
                new ShardNodeDescriptor { NodeId = "node-a", Weight = 1, Region = "iad" },
                new ShardNodeDescriptor { NodeId = "node-b", Weight = 4, Region = "iad" }
            },
            Shards = Enumerable.Range(0, 200)
                .Select(i => new ShardDefinition { ShardId = i.ToString("D3", CultureInfo.InvariantCulture) })
                .ToArray()
        };

        var plan = strategy.Compute(request);
        var perNode = plan.Assignments.GroupBy(a => a.OwnerNodeId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        ((double)perNode["node-b"]).ShouldBeGreaterThan((double)perNode["node-a"]);
        ((double)perNode["node-b"]).ShouldBeGreaterThan(plan.Assignments.Count * 0.65);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void LocalityStrategy_PrefersZoneThenRegion()
    {
        var strategy = new LocalityAwareShardHashStrategy();
        var shards = new[]
        {
            new ShardDefinition { ShardId = "iad-1-01", LocalityHint = "iad/iad-1" },
            new ShardDefinition { ShardId = "iad-zz", LocalityHint = "iad" },
            new ShardDefinition { ShardId = "phx-0", LocalityHint = "phx/phx-1" },
            new ShardDefinition { ShardId = "global-0" }
        };

        var request = new ShardHashRequest
        {
            Namespace = "mesh.payments",
            Nodes = new[]
            {
                new ShardNodeDescriptor { NodeId = "iad-zone-1", Region = "iad", Zone = "iad-1" },
                new ShardNodeDescriptor { NodeId = "iad-zone-2", Region = "iad", Zone = "iad-2" },
                new ShardNodeDescriptor { NodeId = "phx-zone-1", Region = "phx", Zone = "phx-1" }
            },
            Shards = shards
        };

        var plan = strategy.Compute(request);
        var map = plan.Assignments.ToDictionary(a => a.ShardId, a => a.OwnerNodeId, StringComparer.OrdinalIgnoreCase);

        map["iad-1-01"].ShouldBe("iad-zone-1");
        map["phx-0"].ShouldBe("phx-zone-1");
        new[] { "iad-zone-1", "iad-zone-2" }.ShouldContain(map["iad-zz"]);
        map["global-0"].ShouldNotBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Registry_ProvidesBuiltInStrategies()
    {
        var registry = new ShardHashStrategyRegistry();
        registry.RegisteredStrategyIds.ShouldContain(ShardHashStrategyIds.ConsistentRing);
        registry.RegisteredStrategyIds.ShouldContain(ShardHashStrategyIds.Rendezvous);
        registry.RegisteredStrategyIds.ShouldContain(ShardHashStrategyIds.LocalityAware);

        var request = CreateRequest("mesh.registry", shardCount: 8);
        registry.Compute(ShardHashStrategyIds.Rendezvous, request).Assignments.ShouldNotBeEmpty();
    }

    private static ShardHashRequest CreateRequest(string @namespace, int shardCount)
    {
        return new ShardHashRequest
        {
            Namespace = @namespace,
            Nodes = new[]
            {
                new ShardNodeDescriptor { NodeId = "node-a", Weight = 1, Region = "iad" },
                new ShardNodeDescriptor { NodeId = "node-b", Weight = 1.5, Region = "phx" },
                new ShardNodeDescriptor { NodeId = "node-c", Weight = 0.8, Region = "dub" }
            },
            Shards = Enumerable.Range(0, shardCount)
                .Select(i => new ShardDefinition { ShardId = i.ToString("D2", CultureInfo.InvariantCulture) })
                .ToArray()
        };
    }
}
