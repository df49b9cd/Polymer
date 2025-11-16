namespace OmniRelay.Diagnostics;

/// <summary>Provides peer snapshot data for diagnostics endpoints.</summary>
public interface IPeerDiagnosticsProvider
{
    PeerDiagnosticsResponse CreateSnapshot();
}

/// <summary>Response payload for /omnirelay/control/peers.</summary>
public sealed record PeerDiagnosticsResponse(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string LocalNodeId,
    IReadOnlyList<PeerDiagnosticsPeer> Peers);

/// <summary>Represents an individual peer entry.</summary>
public sealed record PeerDiagnosticsPeer(
    string NodeId,
    string Status,
    DateTimeOffset? LastSeen,
    double? RttMs,
    PeerDiagnosticsPeerMetadata Metadata);

/// <summary>Metadata describing peer role, cluster, and connection info.</summary>
public sealed record PeerDiagnosticsPeerMetadata(
    string Role,
    string ClusterId,
    string Region,
    string MeshVersion,
    bool Http3Support,
    string? Endpoint,
    long MetadataVersion,
    IReadOnlyDictionary<string, string> Labels);
