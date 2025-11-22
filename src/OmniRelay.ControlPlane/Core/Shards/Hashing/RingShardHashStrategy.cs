namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Consistent hashing ring strategy that places shards on a sorted hash ring.</summary>
public sealed class RingShardHashStrategy : IShardHashStrategy
{
    private const int VirtualNodesPerWeight = 64;

    public string Id => ShardHashStrategyIds.ConsistentRing;

    public ShardHashPlan Compute(ShardHashRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Nodes.Count == 0)
        {
            throw new ArgumentException("At least one node is required to compute shard assignments.", nameof(request));
        }

        if (request.Shards.Count == 0)
        {
            return new ShardHashPlan(request.Namespace, Id, Array.Empty<ShardAssignment>(), DateTimeOffset.UtcNow);
        }

        var ring = BuildRing(request.Nodes);
        var assignments = new List<ShardAssignment>(request.Shards.Count);
        foreach (var shard in request.Shards)
        {
            var hash = ShardHashingPrimitives.Hash($"{request.Namespace}/{shard.ShardId}");
            var node = LocateNode(ring, hash);
            assignments.Add(new ShardAssignment
            {
                Namespace = request.Namespace,
                ShardId = shard.ShardId,
                OwnerNodeId = node,
                Capacity = shard.Capacity,
                LocalityHint = shard.LocalityHint
            });
        }

        return new ShardHashPlan(request.Namespace, Id, assignments, DateTimeOffset.UtcNow);
    }

    private static string LocateNode(List<RingPoint> ring, ulong hash)
    {
        var index = ring.BinarySearch(new RingPoint(hash, string.Empty), RingPointComparer.Instance);
        if (index < 0)
        {
            index = ~index;
            if (index >= ring.Count)
            {
                index = 0;
            }
        }

        return ring[index].NodeId;
    }

    private static List<RingPoint> BuildRing(IReadOnlyList<ShardNodeDescriptor> nodes)
    {
        var ring = new List<RingPoint>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
            {
                continue;
            }

            var replicas = Math.Max(1, (int)Math.Round(node.Weight * VirtualNodesPerWeight, MidpointRounding.AwayFromZero));
            for (var replica = 0; replica < replicas; replica++)
            {
                var hash = ShardHashingPrimitives.Hash($"{node.NodeId}#{replica}");
                ring.Add(new RingPoint(hash, node.NodeId));
            }
        }

        ring.Sort(RingPointComparer.Instance);
        return ring;
    }

    private readonly record struct RingPoint(ulong Hash, string NodeId);

    private sealed class RingPointComparer : IComparer<RingPoint>
    {
        public static RingPointComparer Instance { get; } = new();

        public int Compare(RingPoint x, RingPoint y)
        {
            var result = x.Hash.CompareTo(y.Hash);
            return result != 0 ? result : string.CompareOrdinal(x.NodeId, y.NodeId);
        }
    }
}
