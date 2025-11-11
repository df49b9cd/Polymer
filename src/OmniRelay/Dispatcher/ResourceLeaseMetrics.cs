using System.Diagnostics.Metrics;

namespace OmniRelay.Dispatcher;

internal static class ResourceLeaseMetrics
{
    private static readonly Meter Meter = new("OmniRelay.Dispatcher.ResourceLease");

    private static readonly Histogram<long> PendingHistogram =
        Meter.CreateHistogram<long>("omnirelay.resourcelease.pending", unit: "items", description: "Observed pending queue depth samples.");

    private static readonly Histogram<long> ActiveHistogram =
        Meter.CreateHistogram<long>("omnirelay.resourcelease.active", unit: "leases", description: "Observed active lease count samples.");

    private static readonly Counter<long> BackpressureTransitions =
        Meter.CreateCounter<long>("omnirelay.resourcelease.backpressure.transitions", unit: "events", description: "Backpressure state transitions for the resource lease queue.");

    internal static void RecordQueueStats(long pending, long active)
    {
        PendingHistogram.Record(pending);
        ActiveHistogram.Record(active);
    }

    internal static void RecordBackpressureState(bool isActive) =>
        BackpressureTransitions.Add(1, KeyValuePair.Create<string, object?>("state", isActive ? "active" : "inactive"));
}
