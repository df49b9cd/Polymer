using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Hugo;
using Polymer.Core;
using Polymer.Errors;

namespace Polymer.Core.Peers;

internal static class PeerMetrics
{
    private static readonly Meter Meter = new("Polymer.Core.Peers");

    private static readonly UpDownCounter<long> InflightCounter =
        Meter.CreateUpDownCounter<long>("polymer.peer.inflight", unit: "requests", description: "In-flight requests per peer.");

    private static readonly Counter<long> SuccessCounter =
        Meter.CreateCounter<long>("polymer.peer.successes", unit: "requests", description: "Successful requests per peer.");

    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>("polymer.peer.failures", unit: "requests", description: "Failed requests per peer.");

    private static readonly Counter<long> LeaseRejectedCounter =
        Meter.CreateCounter<long>("polymer.peer.lease_rejected", unit: "requests", description: "Peer lease attempts rejected by peers.");

    private static readonly Counter<long> PoolExhaustedCounter =
        Meter.CreateCounter<long>("polymer.peer.pool_exhausted", unit: "requests", description: "Times an outbound exhausted all available peers.");

    private static readonly Counter<long> RetryScheduledCounter =
        Meter.CreateCounter<long>("polymer.retry.scheduled", unit: "attempts", description: "Retry attempts scheduled after peer failures.");

    private static readonly Counter<long> RetryExhaustedCounter =
        Meter.CreateCounter<long>("polymer.retry.exhausted", unit: "requests", description: "Requests that exhausted their retry budget.");

    private static readonly Counter<long> RetrySucceededCounter =
        Meter.CreateCounter<long>("polymer.retry.succeeded", unit: "requests", description: "Requests that succeeded after one or more retries.");

    internal static void RecordLeaseAcquired(RequestMeta meta, string peerIdentifier)
    {
        InflightCounter.Add(1, CreatePeerTags(meta, peerIdentifier));
    }

    internal static void RecordLeaseReleased(RequestMeta meta, string peerIdentifier, bool success)
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
    }

    internal static void RecordLeaseRejected(RequestMeta meta, string peerIdentifier, string reason)
    {
        var tags = Append(CreatePeerTags(meta, peerIdentifier), new KeyValuePair<string, object?>("peer.rejection_reason", reason));
        LeaseRejectedCounter.Add(1, tags);
    }

    internal static void RecordPoolExhausted(RequestMeta meta)
    {
        PoolExhaustedCounter.Add(1, CreatePeerTags(meta, peerIdentifier: string.Empty));
    }

    internal static void RecordRetryScheduled(RequestMeta meta, Error error, int attempt, TimeSpan? delay)
    {
        var tags = AppendRetryTags(meta, error, attempt);
        if (delay is { } duration)
        {
            tags = Append(tags, new KeyValuePair<string, object?>("retry.delay_ms", duration.TotalMilliseconds));
        }

        RetryScheduledCounter.Add(1, tags);
    }

    internal static void RecordRetryExhausted(RequestMeta meta, Error error, int attempt)
    {
        RetryExhaustedCounter.Add(1, AppendRetryTags(meta, error, attempt));
    }

    internal static void RecordRetrySucceeded(RequestMeta meta, int attempts)
    {
        var tags = Append(CreatePeerTags(meta, string.Empty), new KeyValuePair<string, object?>("retry.attempts", attempts));
        RetrySucceededCounter.Add(1, tags);
    }

    private static KeyValuePair<string, object?>[] CreatePeerTags(RequestMeta meta, string peerIdentifier)
    {
        return new[]
        {
            new KeyValuePair<string, object?>("rpc.service", meta.Service ?? string.Empty),
            new KeyValuePair<string, object?>("rpc.procedure", meta.Procedure ?? string.Empty),
            new KeyValuePair<string, object?>("rpc.transport", meta.Transport ?? string.Empty),
            new KeyValuePair<string, object?>("rpc.peer", peerIdentifier ?? string.Empty)
        };
    }

    private static KeyValuePair<string, object?>[] AppendRetryTags(RequestMeta meta, Error error, int attempt)
    {
        var tags = CreatePeerTags(meta, string.Empty);
        var status = PolymerErrorAdapter.ToStatus(error);
        tags = Append(tags, new KeyValuePair<string, object?>("error.status", status.ToString()));
        tags = Append(tags, new KeyValuePair<string, object?>("retry.attempt", attempt));
        return tags;
    }

    private static KeyValuePair<string, object?>[] Append(KeyValuePair<string, object?>[] tags, KeyValuePair<string, object?> append)
    {
        var result = new KeyValuePair<string, object?>[tags.Length + 1];
        Array.Copy(tags, result, tags.Length);
        result[^1] = append;
        return result;
    }
}
