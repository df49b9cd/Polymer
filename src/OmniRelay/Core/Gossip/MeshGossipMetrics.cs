using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

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

    private static int _aliveCount;
    private static int _suspectCount;
    private static int _leftCount;

    public static void RecordMemberCounts(int alive, int suspect, int left)
    {
        UpdateGauge(ref _aliveCount, alive, "alive");
        UpdateGauge(ref _suspectCount, suspect, "suspect");
        UpdateGauge(ref _leftCount, left, "left");
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

    private static void UpdateGauge(ref int storage, int newValue, string status)
    {
        var previous = Interlocked.Exchange(ref storage, newValue);
        var delta = newValue - previous;
        if (delta == 0)
        {
            return;
        }

        MemberCounter.Add(delta, new[] { new KeyValuePair<string, object?>("mesh.status", status) });
    }
}
