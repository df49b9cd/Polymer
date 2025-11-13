using OmniRelay.Core.Shards;

namespace OmniRelay.ShardStore.ObjectStorage;

/// <summary>Serialized representation of a shard record plus its audit history.</summary>
public sealed record ShardObjectDocument
{
    public required ShardRecord Record { get; init; }

    public List<ShardHistoryRecord> History { get; init; } = new();
}
