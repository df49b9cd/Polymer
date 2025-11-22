namespace OmniRelay.Core.Shards;

/// <summary>Identifies a shard by namespace and shard identifier.</summary>
public readonly record struct ShardKey(string Namespace, string ShardId)
{
    public override string ToString() => string.Create(Namespace.Length + ShardId.Length + 1, this, static (span, key) =>
    {
        key.Namespace.AsSpan().CopyTo(span);
        span[key.Namespace.Length] = '/';
        key.ShardId.AsSpan().CopyTo(span[(key.Namespace.Length + 1)..]);
    });
}
