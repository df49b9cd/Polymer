namespace OmniRelay.Core.Shards;

/// <summary>Represents a create/update operation for a shard record.</summary>
public sealed class ShardMutationRequest
{
    public required string Namespace { get; init; }

    public required string ShardId { get; init; }

    public required string StrategyId { get; init; }

    public required string OwnerNodeId { get; init; }

    public string? LeaderId { get; init; }

    public double CapacityHint { get; init; }

    public ShardStatus Status { get; init; } = ShardStatus.Active;

    public string? ChangeTicket { get; init; }

    public long? ExpectedVersion { get; init; }

    public ShardChangeMetadata ChangeMetadata { get; init; } = new("system", "unspecified");

    public ShardKey Key => new(Namespace, ShardId);
}
