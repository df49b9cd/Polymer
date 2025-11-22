namespace OmniRelay.Core.Shards;

/// <summary>Represents an immutable audit record describing how a shard changed.</summary>
public sealed record ShardHistoryRecord
{
    public required string Namespace { get; init; }

    public required string ShardId { get; init; }

    public required long Version { get; init; }

    public string StrategyId { get; init; } = string.Empty;

    public required string Actor { get; init; }

    public required string Reason { get; init; }

    public string? ChangeTicket { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string OwnerNodeId { get; init; } = string.Empty;

    public string? PreviousOwnerNodeId { get; init; }

    public double? OwnershipDeltaPercent { get; init; }

    public string? Metadata { get; init; }

    public ShardKey Key => new(Namespace, ShardId);
}
