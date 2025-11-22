namespace OmniRelay.Core.Shards;

/// <summary>Represents the durable ownership metadata for a shard.</summary>
public sealed record ShardRecord
{
    public required string Namespace { get; init; }

    public required string ShardId { get; init; }

    public required string StrategyId { get; init; }

    public required string OwnerNodeId { get; init; }

    public string? LeaderId { get; init; }

    public double CapacityHint { get; init; }

    public ShardStatus Status { get; init; } = ShardStatus.Active;

    public long Version { get; init; }

    public required string Checksum { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? ChangeTicket { get; init; }

    public ShardKey Key => new(Namespace, ShardId);
}
