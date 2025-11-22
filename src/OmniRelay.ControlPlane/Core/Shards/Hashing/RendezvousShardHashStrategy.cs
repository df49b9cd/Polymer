namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Rendezvous (highest random weight) hashing strategy.</summary>
public sealed class RendezvousShardHashStrategy : IShardHashStrategy
{
    public string Id => ShardHashStrategyIds.Rendezvous;

    public ShardHashPlan Compute(ShardHashRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Nodes.Count == 0)
        {
            throw new ArgumentException("At least one node is required to compute shard assignments.", nameof(request));
        }

        var assignments = new List<ShardAssignment>(request.Shards.Count);
        foreach (var shard in request.Shards)
        {
            var owner = SelectOwner(request.Namespace, shard, request.Nodes);
            assignments.Add(new ShardAssignment
            {
                Namespace = request.Namespace,
                ShardId = shard.ShardId,
                OwnerNodeId = owner,
                Capacity = shard.Capacity,
                LocalityHint = shard.LocalityHint
            });
        }

        return new ShardHashPlan(request.Namespace, Id, assignments, DateTimeOffset.UtcNow);
    }

    internal static string SelectOwner(string @namespace, ShardDefinition shard, IReadOnlyList<ShardNodeDescriptor> nodes)
    {
        string? owner = null;
        double bestScore = double.NegativeInfinity;
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
            {
                continue;
            }

            var hash = ShardHashingPrimitives.Hash($"{@namespace}/{shard.ShardId}::{node.NodeId}");
            var normalized = ShardHashingPrimitives.Normalize(hash);
            var weight = Math.Max(0.001, node.Weight);
            var score = normalized * weight;
            if (score > bestScore)
            {
                bestScore = score;
                owner = node.NodeId;
                continue;
            }

            if (Math.Abs(score - bestScore) < 1e-12 && owner is not null && string.CompareOrdinal(node.NodeId, owner) < 0)
            {
                owner = node.NodeId;
            }
        }

        if (owner is null)
        {
            throw new InvalidOperationException("No eligible nodes found for shard hashing.");
        }

        return owner;
    }
}
