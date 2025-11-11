using System.Collections.Immutable;

namespace OmniRelay.Core.Peers;

/// <summary>Diagnostic payload describing per-peer lease health and summary metrics.</summary>
public sealed record PeerLeaseHealthDiagnostics(
    ImmutableArray<PeerLeaseHealthSnapshot> Peers,
    PeerLeaseHealthSummary Summary)
{
    public ImmutableArray<PeerLeaseHealthSnapshot> Peers { get; init; } = Peers;

    public PeerLeaseHealthSummary Summary { get; init; } = Summary;

    /// <summary>Creates a diagnostics payload from the supplied snapshots.</summary>
    public static PeerLeaseHealthDiagnostics FromSnapshots(ImmutableArray<PeerLeaseHealthSnapshot> snapshots)
    {
        if (snapshots.IsDefault)
        {
            snapshots = ImmutableArray<PeerLeaseHealthSnapshot>.Empty;
        }

        var eligible = 0;
        var unhealthy = 0;
        var pendingReassignments = 0;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.IsHealthy)
            {
                eligible++;
            }
            else
            {
                unhealthy++;
            }

            pendingReassignments += snapshot.PendingReassignments;
        }

        return new PeerLeaseHealthDiagnostics(
            snapshots,
            new PeerLeaseHealthSummary(eligible, unhealthy, pendingReassignments));
    }
}

/// <summary>Aggregated lease health counts used by diagnostics dashboards.</summary>
public sealed record PeerLeaseHealthSummary(int EligiblePeers, int UnhealthyPeers, int PendingReassignments)
{
    public int EligiblePeers { get; init; } = EligiblePeers;

    public int UnhealthyPeers { get; init; } = UnhealthyPeers;

    public int PendingReassignments { get; init; } = PendingReassignments;
}
