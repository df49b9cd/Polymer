using System.Diagnostics.Metrics;
using Hugo;

namespace OmniRelay.Dispatcher;

internal static class TableLeaseMetrics
{
    private static readonly Meter Meter = new("OmniRelay.Dispatcher.TableLease");

    private static readonly Histogram<long> PendingHistogram =
        Meter.CreateHistogram<long>("omnirelay.tablelease.pending", unit: "items", description: "Observed pending queue depth samples.");

    private static readonly Histogram<long> ActiveHistogram =
        Meter.CreateHistogram<long>("omnirelay.tablelease.active", unit: "leases", description: "Observed active lease count samples.");

    private static readonly Counter<long> BackpressureTransitions =
        Meter.CreateCounter<long>("omnirelay.tablelease.backpressure.transitions", unit: "events", description: "Backpressure state transitions for the table lease queue.");

    internal static void RecordQueueStats(long pending, long active)
    {
        PendingHistogram.Record(pending);
        ActiveHistogram.Record(active);
    }

    internal static void RecordBackpressureState(bool isActive) =>
        BackpressureTransitions.Add(1);
}
