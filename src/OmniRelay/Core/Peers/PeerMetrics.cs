using System.Diagnostics;
using System.Diagnostics.Metrics;
using Hugo;
using OmniRelay.Errors;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Internal peer and retry metric helpers used by transport and middleware components.
/// </summary>
internal static class PeerMetrics
{
    private static readonly Meter Meter = new("OmniRelay.Core.Peers");

    private static readonly UpDownCounter<long> InflightCounter =
        Meter.CreateUpDownCounter<long>("omnirelay.peer.inflight", unit: "requests", description: "In-flight requests per peer.");

    private static readonly Counter<long> SuccessCounter =
        Meter.CreateCounter<long>("omnirelay.peer.successes", unit: "requests", description: "Successful requests per peer.");

    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>("omnirelay.peer.failures", unit: "requests", description: "Failed requests per peer.");

    private static readonly Counter<long> LeaseRejectedCounter =
        Meter.CreateCounter<long>("omnirelay.peer.lease_rejected", unit: "requests", description: "Peer lease attempts rejected by peers.");

    private static readonly Counter<long> PoolExhaustedCounter =
        Meter.CreateCounter<long>("omnirelay.peer.pool_exhausted", unit: "requests", description: "Times an outbound exhausted all available peers.");

    private static readonly Histogram<double> LeaseDurationHistogram =
        Meter.CreateHistogram<double>("omnirelay.peer.lease.duration", unit: "ms", description: "Observed lease durations by peer.");

    private static readonly Counter<long> RetryScheduledCounter =
        Meter.CreateCounter<long>("omnirelay.retry.scheduled", unit: "attempts", description: "Retry attempts scheduled after peer failures.");

    private static readonly Counter<long> RetryExhaustedCounter =
        Meter.CreateCounter<long>("omnirelay.retry.exhausted", unit: "requests", description: "Requests that exhausted their retry budget.");

    private static readonly Counter<long> RetrySucceededCounter =
        Meter.CreateCounter<long>("omnirelay.retry.succeeded", unit: "requests", description: "Requests that succeeded after one or more retries.");

    private static readonly Counter<long> LeaseAssignmentCounter =
        Meter.CreateCounter<long>("omnirelay.peer.lease_assignments", unit: "leases", description: "Resource lease assignments per peer.");

    private static readonly Counter<long> LeaseHeartbeatCounter =
        Meter.CreateCounter<long>("omnirelay.peer.lease_heartbeats", unit: "signals", description: "Heartbeats received from peers holding resource leases.");

    private static readonly Counter<long> LeaseDisconnectCounter =
        Meter.CreateCounter<long>("omnirelay.peer.lease_disconnects", unit: "signals", description: "Disconnect signals detected for peers with outstanding leases.");

    internal static void RecordLeaseAcquired(RequestMeta meta, string peerIdentifier) => InflightCounter.Add(1, CreatePeerTags(meta, peerIdentifier));

    internal static void RecordLeaseReleased(RequestMeta meta, string peerIdentifier, bool success, double durationMilliseconds)
    {
        var tags = CreatePeerTags(meta, peerIdentifier);
        InflightCounter.Add(-1, tags);

        if (success)
        {
            SuccessCounter.Add(1, tags);
        }
        else
        {
            FailureCounter.Add(1, tags);
        }

        LeaseDurationHistogram.Record(durationMilliseconds, tags);
    }

    internal static void RecordLeaseRejected(RequestMeta meta, string peerIdentifier, string reason)
    {
        var tags = CreatePeerTags(meta, peerIdentifier);
        tags.Add("peer.rejection_reason", reason);
        LeaseRejectedCounter.Add(1, tags);
    }

    internal static void RecordPoolExhausted(RequestMeta meta) =>
        PoolExhaustedCounter.Add(1, CreatePeerTags(meta, peerIdentifier: string.Empty));

    internal static void RecordRetryScheduled(RequestMeta meta, Error error, int attempt, TimeSpan? delay)
    {
        var tags = CreateRetryTags(meta, error, attempt);
        if (delay is { } duration)
        {
            tags.Add("retry.delay_ms", duration.TotalMilliseconds);
        }

        RetryScheduledCounter.Add(1, tags);
    }

    internal static void RecordRetryExhausted(RequestMeta meta, Error error, int attempt) =>
        RetryExhaustedCounter.Add(1, CreateRetryTags(meta, error, attempt));

    internal static void RecordRetrySucceeded(RequestMeta meta, int attempts)
    {
        var tags = CreatePeerTags(meta, string.Empty);
        tags.Add("retry.attempts", attempts);
        RetrySucceededCounter.Add(1, tags);
    }

    internal static void RecordLeaseAssignmentSignal(string peerIdentifier, string? resourceType, string? resourceId)
    {
        var tags = new TagList { { "rpc.peer", peerIdentifier ?? string.Empty } };
        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            tags.Add("lease.resource_type", resourceType!);
        }

        if (!string.IsNullOrWhiteSpace(resourceId))
        {
            tags.Add("lease.resource_id", resourceId!);
        }

        LeaseAssignmentCounter.Add(1, tags);
    }

    internal static void RecordLeaseHeartbeatSignal(string peerIdentifier) =>
        LeaseHeartbeatCounter.Add(1, new TagList { { "rpc.peer", peerIdentifier ?? string.Empty } });

    internal static void RecordLeaseDisconnectSignal(string peerIdentifier, string? reason = null)
    {
        var tags = new TagList { { "rpc.peer", peerIdentifier ?? string.Empty } };
        if (!string.IsNullOrWhiteSpace(reason))
        {
            tags.Add("lease.disconnect.reason", reason!);
        }

        LeaseDisconnectCounter.Add(1, tags);
    }

    private static TagList CreatePeerTags(RequestMeta meta, string peerIdentifier)
    {
        var tags = new TagList
        {
            { "rpc.service", meta.Service ?? string.Empty },
            { "rpc.procedure", meta.Procedure ?? string.Empty },
            { "rpc.transport", meta.Transport ?? string.Empty },
            { "rpc.peer", peerIdentifier ?? string.Empty }
        };

        return tags;
    }

    private static TagList CreateRetryTags(RequestMeta meta, Error error, int attempt)
    {
        var tags = CreatePeerTags(meta, string.Empty);
        var status = OmniRelayErrorAdapter.ToStatus(error);
        tags.Add("error.status", status.ToString());
        tags.Add("retry.attempt", attempt);
        return tags;
    }
}
