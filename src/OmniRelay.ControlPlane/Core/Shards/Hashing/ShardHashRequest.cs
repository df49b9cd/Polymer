namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Input used by hash strategies to compute deterministic shard assignments.</summary>
public sealed record ShardHashRequest
{
    public required string Namespace { get; init; }

    public required IReadOnlyList<ShardDefinition> Shards { get; init; }

    public required IReadOnlyList<ShardNodeDescriptor> Nodes { get; init; }
}
