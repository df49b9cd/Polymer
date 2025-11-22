namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Strategy for deterministically assigning shards to nodes.</summary>
public interface IShardHashStrategy
{
    string Id { get; }

    ShardHashPlan Compute(ShardHashRequest request);
}
