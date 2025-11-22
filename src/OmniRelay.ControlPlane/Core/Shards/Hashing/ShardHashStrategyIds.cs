namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Identifiers for built-in shard hashing strategies.</summary>
public static class ShardHashStrategyIds
{
    public const string ConsistentRing = "ring";

    public const string Rendezvous = "rendezvous";

    public const string LocalityAware = "locality";
}
