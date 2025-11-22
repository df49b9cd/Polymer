using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OmniRelay.Core.Gossip;

/// <summary>Represents the advertised metadata for a gossip participant.</summary>
public sealed record class MeshGossipMemberMetadata
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("clusterId")]
    public string ClusterId { get; init; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; init; } = string.Empty;

    [JsonPropertyName("meshVersion")]
    public string MeshVersion { get; init; } = string.Empty;

    [JsonPropertyName("http3Support")]
    public bool Http3Support { get; init; } = true;

    [JsonPropertyName("metadataVersion")]
    public long MetadataVersion { get; init; } = 1;

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; } = null;

    public MeshGossipMemberMetadata WithLabels(IReadOnlyDictionary<string, string> labels) =>
        this with { Labels = labels };
}

/// <summary>Snapshot for a peer included in membership/gossip payloads.</summary>
public sealed class MeshGossipMemberSnapshot
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public MeshGossipMemberStatus Status { get; init; } = MeshGossipMemberStatus.Alive;

    [JsonPropertyName("lastSeen")]
    public DateTimeOffset? LastSeen { get; init; }
        = null;

    [JsonPropertyName("rttMs")]
    public double? RoundTripTimeMs { get; init; }
        = null;

    [JsonPropertyName("metadata")]
    public MeshGossipMemberMetadata Metadata { get; init; } = new();
}

/// <summary>Enum representing membership states.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MeshGossipMemberStatus>))]
public enum MeshGossipMemberStatus
{
    Alive,
    Suspect,
    Left
}

/// <summary>Represents the cluster view served to diagnostics/consumers.</summary>
public sealed record MeshGossipClusterView(
    DateTimeOffset GeneratedAt,
    ImmutableArray<MeshGossipMemberSnapshot> Members,
    string LocalNodeId,
    string SchemaVersion);

/// <summary>Abstraction exposed to the data-plane for read-only membership snapshots.</summary>
public interface IMeshMembershipSnapshotProvider
{
    MeshGossipClusterView Snapshot();
}
