using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace OmniRelay.Core.Peers;

/// <summary>Exposes observable gauges for peer lease health snapshots.</summary>
internal static class PeerLeaseHealthMetrics
{
    private static readonly Meter Meter = new("OmniRelay.Core.Peers");
    private static readonly ConcurrentBag<PeerLeaseHealthTracker> Trackers = [];

    static PeerLeaseHealthMetrics()
    {
        Meter.CreateObservableGauge(
            "omnirelay.peer.lease.healthy",
            () => new Measurement<long>(AggregateSummary().EligiblePeers));

        Meter.CreateObservableGauge(
            "omnirelay.peer.lease.unhealthy",
            () => new Measurement<long>(AggregateSummary().UnhealthyPeers));

        Meter.CreateObservableGauge(
            "omnirelay.peer.lease.pending_reassignments",
            () => new Measurement<long>(AggregateSummary().PendingReassignments));
    }

    internal static void RegisterTracker(PeerLeaseHealthTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        Trackers.Add(tracker);
    }

    private static PeerLeaseHealthSummary AggregateSummary()
    {
        var healthy = 0;
        var unhealthy = 0;
        var pending = 0;

        foreach (var tracker in Trackers)
        {
            var summary = tracker.GetSummary();
            healthy += summary.EligiblePeers;
            unhealthy += summary.UnhealthyPeers;
            pending += summary.PendingReassignments;
        }

        return new PeerLeaseHealthSummary(healthy, unhealthy, pending);
    }
}
