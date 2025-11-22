namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Defines metadata about an individual shard used for planning ownership.</summary>
public sealed record ShardDefinition
{
    public required string ShardId { get; init; }

    public double Capacity { get; init; } = 1d;

    public string? LocalityHint { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
