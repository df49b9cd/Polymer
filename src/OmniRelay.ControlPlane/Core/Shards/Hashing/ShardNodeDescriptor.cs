namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Describes a mesh node that can own shards for hashing strategies.</summary>
public sealed record ShardNodeDescriptor
{
    public required string NodeId { get; init; }

    public double Weight { get; init; } = 1;

    public string? Region { get; init; }

    public string? Zone { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
