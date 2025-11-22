using System.Text.Json.Serialization;

namespace OmniRelay.Core.Gossip;

/// <summary>Envelope exchanged between gossip peers.</summary>
public sealed class MeshGossipEnvelope
{
    [JsonPropertyName("schema")]
    public string SchemaVersion { get; init; } = MeshGossipOptions.CurrentSchemaVersion;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }
        = 0;

    [JsonPropertyName("sender")]
    public MeshGossipMemberMetadata Sender { get; init; } = new();

    [JsonPropertyName("members")]
    public IReadOnlyList<MeshGossipMemberSnapshot> Members { get; init; } = [];
}
