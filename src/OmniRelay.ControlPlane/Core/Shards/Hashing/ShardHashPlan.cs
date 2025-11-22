namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Represents the computed shard assignments for a namespace.</summary>
public sealed record ShardHashPlan(
    string Namespace,
    string StrategyId,
    IReadOnlyList<ShardAssignment> Assignments,
    DateTimeOffset GeneratedAt);
