namespace OmniRelay.Core.Shards;

/// <summary>Exception thrown when a shard mutation fails optimistic concurrency checks.</summary>
public sealed class ShardConcurrencyException : InvalidOperationException
{
    public ShardConcurrencyException(string message) : base(message)
    {
    }
}
