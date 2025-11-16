using System.Diagnostics.Metrics;

namespace OmniRelay.Diagnostics;

/// <summary>Metrics helpers for peer lease health signals.</summary>
internal static class PeerLeaseHealthSignals
{
    private static readonly Meter Meter = new("OmniRelay.Diagnostics.PeerHealth");

    private static readonly Counter<long> LeaseAssignmentCounter =
        Meter.CreateCounter<long>("omnirelay.peer.lease_assignments", unit: "leases", description: "Resource lease assignments per peer.");

    private static readonly Counter<long> LeaseHeartbeatCounter =
        Meter.CreateCounter<long>("omnirelay.peer.lease_heartbeats", unit: "signals", description: "Heartbeats received from peers holding resource leases.");

    private static readonly Counter<long> LeaseDisconnectCounter =
        Meter.CreateCounter<long>("omnirelay.peer.lease_disconnects", unit: "signals", description: "Disconnect signals detected for peers with outstanding leases.");

    internal static void RecordLeaseAssignment(string peerIdentifier, string? resourceType, string? resourceId)
    {
        var tags = new KeyValuePair<string, object?>[3];
        var index = 0;
        tags[index++] = new KeyValuePair<string, object?>("rpc.peer", peerIdentifier ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            tags[index++] = new KeyValuePair<string, object?>("lease.resource_type", resourceType!);
        }

        if (!string.IsNullOrWhiteSpace(resourceId))
        {
            tags[index++] = new KeyValuePair<string, object?>("lease.resource_id", resourceId!);
        }

        LeaseAssignmentCounter.Add(1, tags.AsSpan(0, index));
    }

    internal static void RecordLeaseHeartbeat(string peerIdentifier)
    {
        var tags = new[] { new KeyValuePair<string, object?>("rpc.peer", peerIdentifier ?? string.Empty) };
        LeaseHeartbeatCounter.Add(1, tags);
    }

    internal static void RecordLeaseDisconnect(string peerIdentifier, string? reason)
    {
        var tags = new KeyValuePair<string, object?>[2];
        var index = 0;
        tags[index++] = new KeyValuePair<string, object?>("rpc.peer", peerIdentifier ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            tags[index++] = new KeyValuePair<string, object?>("lease.disconnect.reason", reason!);
        }

        LeaseDisconnectCounter.Add(1, tags.AsSpan(0, index));
    }
}
