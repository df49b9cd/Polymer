namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Represents the owner chosen for a shard by a hashing strategy.</summary>
public sealed record ShardAssignment
{
    public required string Namespace { get; init; }

    public required string ShardId { get; init; }

    public required string OwnerNodeId { get; init; }

    public double Capacity { get; init; } = 1d;

    public string? LeaderId { get; init; }

    public string? LocalityHint { get; init; }

    public OmniRelay.Core.Shards.ShardKey Key => new(Namespace, ShardId);
}
