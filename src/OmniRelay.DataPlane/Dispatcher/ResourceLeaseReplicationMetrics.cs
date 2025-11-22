using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OmniRelay.Dispatcher;

/// <summary>Records mesh-wide replication metrics (event volume and lag).</summary>
internal static class ResourceLeaseReplicationMetrics
{
    private static readonly Meter Meter = new("OmniRelay.Dispatcher.ResourceLeaseReplication");

    private static readonly Counter<long> EventCounter =
        Meter.CreateCounter<long>(
            "omnirelay.resourcelease.replication.events",
            unit: "events",
            description: "Replication events published by a ResourceLease dispatcher.");

    private static readonly Histogram<double> LagHistogram =
        Meter.CreateHistogram<double>(
            "omnirelay.resourcelease.replication.lag",
            unit: "ms",
            description: "End-to-end replication lag between event creation and sink application.");

    internal static void RecordReplicationEvent(ResourceLeaseReplicationEvent replicationEvent)
    {
        var tags = CreateTags(replicationEvent);
        EventCounter.Add(1, tags);
    }

    internal static void RecordReplicationLag(ResourceLeaseReplicationEvent replicationEvent, double lagMilliseconds)
    {
        if (double.IsNaN(lagMilliseconds) || double.IsInfinity(lagMilliseconds) || lagMilliseconds < 0)
        {
            return;
        }

        var tags = CreateTags(replicationEvent);
        LagHistogram.Record(lagMilliseconds, tags);
    }

    private static TagList CreateTags(ResourceLeaseReplicationEvent replicationEvent)
    {
        var tags = new TagList
        {
            { "lease.event_type", replicationEvent.EventType.ToString() }
        };

        if (!string.IsNullOrWhiteSpace(replicationEvent.PeerId))
        {
            tags.Add("rpc.peer", replicationEvent.PeerId!);
        }

        if (replicationEvent.Metadata.TryGetValue("shard.id", out var shard) && !string.IsNullOrWhiteSpace(shard))
        {
            tags.Add("shard.id", shard!);
        }

        return tags;
    }
}
