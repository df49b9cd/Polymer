using System;
using System.Collections.Immutable;
using OmniRelay.Core.Peers;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class PeerLeaseHealthDiagnosticsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void FromSnapshots_ComputesSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = ImmutableArray.Create(
            new PeerLeaseHealthSnapshot(
                "healthy",
                now,
                LastDisconnect: null,
                IsHealthy: true,
                ActiveAssignments: 1,
                PendingReassignments: 1,
                ActiveLeases: ImmutableArray<PeerLeaseHandle>.Empty,
                Metadata: ImmutableDictionary<string, string>.Empty),
            new PeerLeaseHealthSnapshot(
                "unhealthy",
                now,
                LastDisconnect: now,
                IsHealthy: false,
                ActiveAssignments: 0,
                PendingReassignments: 2,
                ActiveLeases: ImmutableArray<PeerLeaseHandle>.Empty,
                Metadata: ImmutableDictionary<string, string>.Empty));

        var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(snapshots);

        Assert.Equal(1, diagnostics.Summary.EligiblePeers);
        Assert.Equal(1, diagnostics.Summary.UnhealthyPeers);
        Assert.Equal(3, diagnostics.Summary.PendingReassignments);
        Assert.Equal(2, diagnostics.Peers.Length);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void FromSnapshots_DefaultArrayHandled()
    {
        var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(default);
        Assert.Equal(0, diagnostics.Summary.EligiblePeers);
        Assert.Equal(0, diagnostics.Summary.UnhealthyPeers);
        Assert.Equal(0, diagnostics.Summary.PendingReassignments);
        Assert.Empty(diagnostics.Peers);
    }
}
