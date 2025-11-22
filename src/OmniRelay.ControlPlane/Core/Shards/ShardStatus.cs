namespace OmniRelay.Core.Shards;

/// <summary>Represents the lifecycle state of a shard assignment.</summary>
public enum ShardStatus
{
    Active = 0,
    Draining = 1,
    Paused = 2,
    Disabled = 3
}
