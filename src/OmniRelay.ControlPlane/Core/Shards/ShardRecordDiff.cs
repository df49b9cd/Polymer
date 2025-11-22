namespace OmniRelay.Core.Shards;

/// <summary>Represents the difference between shard versions for streaming observers.</summary>
public sealed record ShardRecordDiff(
    long Position,
    ShardRecord Current,
    ShardRecord? Previous,
    ShardHistoryRecord? History = null);
