namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Prefers nodes that share the shard's locality hint (zone or region) before falling back to global rendezvous hashing.</summary>
public sealed class LocalityAwareShardHashStrategy : IShardHashStrategy
{
    public string Id => ShardHashStrategyIds.LocalityAware;

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
            var candidates = FilterCandidates(request.Nodes, shard);
            var owner = RendezvousShardHashStrategy.SelectOwner(request.Namespace, shard, candidates);
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

    private static IReadOnlyList<ShardNodeDescriptor> FilterCandidates(IReadOnlyList<ShardNodeDescriptor> nodes, ShardDefinition shard)
    {
        if (string.IsNullOrWhiteSpace(shard.LocalityHint))
        {
            return nodes;
        }

        var parsed = ParseHint(shard.LocalityHint);
        var buffer = new List<ShardNodeDescriptor>();
        if (!string.IsNullOrEmpty(parsed.Zone))
        {
            buffer.AddRange(
                nodes.Where(node =>
                    !string.IsNullOrEmpty(node.Zone) &&
                    string.Equals(node.Zone, parsed.Zone, StringComparison.OrdinalIgnoreCase)
                )
            );
        }

        if (buffer.Count > 0)
        {
            return buffer;
        }

        if (!string.IsNullOrEmpty(parsed.Region))
        {
            foreach (var node in nodes)
            {
                if (!string.IsNullOrEmpty(node.Region) && string.Equals(node.Region, parsed.Region, StringComparison.OrdinalIgnoreCase))
                {
                    buffer.Add(node);
                }
            }
        }

        return buffer.Count > 0 ? buffer : nodes;
    }

    private static (string? Region, string? Zone) ParseHint(string hint)
    {
        var trimmed = hint.Trim();
        if (trimmed.Length == 0)
        {
            return (null, null);
        }

        if (trimmed.StartsWith("zone:", StringComparison.OrdinalIgnoreCase))
        {
            return (null, trimmed[5..].Trim());
        }

        if (trimmed.StartsWith("region:", StringComparison.OrdinalIgnoreCase))
        {
            return (trimmed[7..].Trim(), null);
        }

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var parts = trimmed.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var region = parts.Length > 0 ? parts[0] : null;
            var zone = parts.Length > 1 ? parts[1] : null;
            return (region, zone);
        }

        return (trimmed, null);
    }
}
