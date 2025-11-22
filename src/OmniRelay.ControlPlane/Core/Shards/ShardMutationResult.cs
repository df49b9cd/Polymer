namespace OmniRelay.Core.Shards;

/// <summary>Result of a shard mutation operation.</summary>
public sealed record ShardMutationResult(
    ShardRecord Record,
    ShardHistoryRecord History,
    bool Created);
