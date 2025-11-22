using System.Collections.Immutable;

namespace OmniRelay.Diagnostics;

/// <summary>
/// Snapshot describing per-peer SafeTaskQueue lease health.
/// </summary>
public sealed record PeerLeaseHealthSnapshot(
    string PeerId,
    DateTimeOffset LastHeartbeat,
    DateTimeOffset? LastDisconnect,
    bool IsHealthy,
    int ActiveAssignments,
    int PendingReassignments,
    ImmutableArray<PeerLeaseHandle> ActiveLeases,
    ImmutableDictionary<string, string> Metadata)
{
    public string PeerId { get; init; } = PeerId;

    public DateTimeOffset LastHeartbeat { get; init; } = LastHeartbeat;

    public DateTimeOffset? LastDisconnect { get; init; } = LastDisconnect;

    public bool IsHealthy { get; init; } = IsHealthy;

    public int ActiveAssignments { get; init; } = ActiveAssignments;

    public int PendingReassignments { get; init; } = PendingReassignments;

    public ImmutableArray<PeerLeaseHandle> ActiveLeases { get; init; } = ActiveLeases;

    public ImmutableDictionary<string, string> Metadata { get; init; } = Metadata;
}
