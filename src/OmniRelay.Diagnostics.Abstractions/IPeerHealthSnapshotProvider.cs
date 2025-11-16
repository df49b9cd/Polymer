using System.Collections.Immutable;

namespace OmniRelay.Diagnostics;

/// <summary>
/// Supplies peer lease health snapshots for routing and diagnostics components.
/// </summary>
public interface IPeerHealthSnapshotProvider
{
    /// <summary>Determines whether the specified peer is eligible to receive new work.</summary>
    bool IsPeerEligible(string peerId);

    /// <summary>Returns the current snapshot for all tracked peers.</summary>
    ImmutableArray<PeerLeaseHealthSnapshot> Snapshot();
}
