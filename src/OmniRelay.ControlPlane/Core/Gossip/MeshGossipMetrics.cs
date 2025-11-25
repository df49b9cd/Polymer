using System.Diagnostics.Metrics;

namespace OmniRelay.Core.Gossip;

/// <summary>Metrics helpers for the gossip agent.</summary>
internal static class MeshGossipMetrics
{
    private static readonly Meter Meter = new("OmniRelay.Core.Gossip");

    private static readonly UpDownCounter<long> MemberCounter =
        Meter.CreateUpDownCounter<long>("mesh_gossip_members", unit: "peers", description: "Number of peers in each membership state.");

    private static readonly Histogram<double> RoundTripHistogram =
        Meter.CreateHistogram<double>("mesh_gossip_rtt_ms", unit: "ms", description: "Observed gossip RTT measurements.");

    private static readonly Counter<long> MessageCounter =
        Meter.CreateCounter<long>("mesh_gossip_messages_total", unit: "messages", description: "Total gossip messages exchanged.");

    private static readonly UpDownCounter<long> ViewCounter =
        Meter.CreateUpDownCounter<long>("mesh_gossip_view_size", unit: "peers", description: "Current active/passive view sizes.");

    private static readonly Histogram<long> FanoutHistogram =
        Meter.CreateHistogram<long>("mesh_gossip_fanout", unit: "peers", description: "Fanout state per gossip round.");

    private static readonly Counter<long> DuplicateTargetCounter =
        Meter.CreateCounter<long>("mesh_gossip_duplicate_targets", unit: "peers", description: "Count of duplicate gossip targets per round.");

    private static int _aliveCount;
    private static int _suspectCount;
    private static int _leftCount;
    private static int _activeView;
    private static int _passiveView;

    public static void RecordMemberCounts(int alive, int suspect, int left)
    {
        UpdateGauge(ref _aliveCount, alive, "alive", MemberCounter);
        UpdateGauge(ref _suspectCount, suspect, "suspect", MemberCounter);
        UpdateGauge(ref _leftCount, left, "left", MemberCounter);
    }

    public static void RecordViewSizes(int active, int passive)
    {
        UpdateGauge(ref _activeView, active, "active", ViewCounter);
        UpdateGauge(ref _passiveView, passive, "passive", ViewCounter);
    }

    public static void RecordFanout(int computed, int attempted, int duplicates)
    {
        FanoutHistogram.Record(attempted,
        [
            new KeyValuePair<string, object?>("mesh.fanout.computed", computed),
            new KeyValuePair<string, object?>("mesh.fanout.attempted", attempted)
        ]);

        if (duplicates > 0)
        {
            DuplicateTargetCounter.Add(duplicates);
        }
    }

    public static void RecordRoundTrip(string peerId, double milliseconds)
    {
        var tags = new[] { new KeyValuePair<string, object?>("mesh.peer", peerId) };
        RoundTripHistogram.Record(milliseconds, tags);
    }

    public static void RecordMessage(string direction, string outcome)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("mesh.direction", direction),
            new KeyValuePair<string, object?>("mesh.outcome", outcome)
        };
        MessageCounter.Add(1, tags);
    }

    private static void UpdateGauge(ref int storage, int newValue, string status, UpDownCounter<long> counter)
    {
        var previous = Interlocked.Exchange(ref storage, newValue);
        var delta = newValue - previous;
        if (delta == 0)
        {
            return;
        }

        counter.Add(delta, [new KeyValuePair<string, object?>("mesh.status", status)]);
    }
}
