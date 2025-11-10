using System.Diagnostics.Metrics;
using System.Threading;

namespace OmniRelay.Core.Peers;

/// <summary>Exposes observable gauges for peer lease health snapshots.</summary>
internal static class PeerLeaseHealthMetrics
{
    private static readonly Meter Meter = new("OmniRelay.Core.Peers");
    private static long _healthy;
    private static long _unhealthy;
    private static long _pendingReassignments;

    static PeerLeaseHealthMetrics()
    {
        Meter.CreateObservableGauge(
            "omnirelay.peer.lease.healthy",
            () => new Measurement<long>(Volatile.Read(ref _healthy)));

        Meter.CreateObservableGauge(
            "omnirelay.peer.lease.unhealthy",
            () => new Measurement<long>(Volatile.Read(ref _unhealthy)));

        Meter.CreateObservableGauge(
            "omnirelay.peer.lease.pending_reassignments",
            () => new Measurement<long>(Volatile.Read(ref _pendingReassignments)));
    }

    internal static void UpdateSnapshot(int healthy, int unhealthy, int pendingReassignments)
    {
        Volatile.Write(ref _healthy, healthy);
        Volatile.Write(ref _unhealthy, unhealthy);
        Volatile.Write(ref _pendingReassignments, pendingReassignments);
    }
}
