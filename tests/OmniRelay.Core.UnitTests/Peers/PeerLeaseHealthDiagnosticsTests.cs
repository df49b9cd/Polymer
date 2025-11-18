using System.Collections.Immutable;
using OmniRelay.Diagnostics;
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
                ActiveLeases: [],
                Metadata: []),
            new PeerLeaseHealthSnapshot(
                "unhealthy",
                now,
                LastDisconnect: now,
                IsHealthy: false,
                ActiveAssignments: 0,
                PendingReassignments: 2,
                ActiveLeases: [],
                Metadata: []));

        var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(snapshots);

        diagnostics.Summary.EligiblePeers.ShouldBe(1);
        diagnostics.Summary.UnhealthyPeers.ShouldBe(1);
        diagnostics.Summary.PendingReassignments.ShouldBe(3);
        diagnostics.Peers.Length.ShouldBe(2);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void FromSnapshots_DefaultArrayHandled()
    {
        var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(default);
        diagnostics.Summary.EligiblePeers.ShouldBe(0);
        diagnostics.Summary.UnhealthyPeers.ShouldBe(0);
        diagnostics.Summary.PendingReassignments.ShouldBe(0);
        diagnostics.Peers.ShouldBeEmpty();
    }
}
