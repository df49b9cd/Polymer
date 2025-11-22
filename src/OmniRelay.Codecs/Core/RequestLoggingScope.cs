using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core;

/// <summary>
/// Utilities for producing structured logging scopes for RPC requests.
/// </summary>
public static class RequestLoggingScope
{
    private static readonly string[] RequestIdHeaderKeys =
    [
        "x-request-id",
        "request-id",
        "rpc-request-id"
    ];

    /// <summary>
    /// Begins a logging scope enriched with RPC and network attributes.
    /// </summary>
    public static IDisposable? Begin(
        ILogger logger,
        RequestMeta meta,
        ResponseMeta? responseMeta = null,
        Activity? activity = null,
        IEnumerable<KeyValuePair<string, object?>>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(meta);

        var items = Create(meta, responseMeta, activity, extra);
        return items.Count == 0 ? null : logger.BeginScope(items);
    }

    /// <summary>
    /// Creates a list of key-value pairs suitable for logging scope enrichment.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> Create(
        RequestMeta meta,
        ResponseMeta? responseMeta = null,
        Activity? activity = null,
        IEnumerable<KeyValuePair<string, object?>>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var scopeItems = new List<KeyValuePair<string, object?>>(12)
        {
            new("rpc.service", meta.Service),
            new("rpc.procedure", meta.Procedure ?? string.Empty),
            new("rpc.transport", meta.Transport ?? "unknown"),
            new("rpc.caller", meta.Caller ?? string.Empty)
        };

        if (!string.IsNullOrEmpty(meta.Encoding))
        {
            scopeItems.Add(new KeyValuePair<string, object?>("rpc.request_encoding", meta.Encoding));
        }

        if (responseMeta?.Encoding is { Length: > 0 } responseEncoding)
        {
            scopeItems.Add(new KeyValuePair<string, object?>("rpc.response_encoding", responseEncoding));
        }

        if (TryGetRequestId(meta, out var requestId))
        {
            scopeItems.Add(new KeyValuePair<string, object?>("rpc.request_id", requestId));
        }

        if (meta.Headers.TryGetValue("rpc.protocol", out var protocol) && !string.IsNullOrWhiteSpace(protocol))
        {
            scopeItems.Add(new KeyValuePair<string, object?>("rpc.protocol", protocol));

            if (protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                var version = protocol.Length > 5 ? protocol[5..] : string.Empty;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    scopeItems.Add(new KeyValuePair<string, object?>("network.protocol.version", version));
                }

                scopeItems.Add(new KeyValuePair<string, object?>("network.protocol.name", "http"));

                // Infer transport for HTTP protocol variants
                if (version.StartsWith('3'))
                {
                    scopeItems.Add(new KeyValuePair<string, object?>("network.transport", "quic"));
                }
                else
                {
                    scopeItems.Add(new KeyValuePair<string, object?>("network.transport", "tcp"));
                }
            }
        }

        var currentActivity = activity ?? Activity.Current;
        if (TryGetPeer(meta, currentActivity, out var peerValue, out var peerPort))
        {
            if (!string.IsNullOrEmpty(peerValue))
            {
                scopeItems.Add(new KeyValuePair<string, object?>("rpc.peer", peerValue));
            }

            if (peerPort.HasValue)
            {
                scopeItems.Add(new KeyValuePair<string, object?>("rpc.peer_port", peerPort.Value));
            }
        }

        if (currentActivity is not null)
        {
            var traceId = currentActivity.TraceId.ToString();
            if (!string.IsNullOrWhiteSpace(traceId))
            {
                scopeItems.Add(new KeyValuePair<string, object?>("activity.trace_id", traceId));
            }

            var spanId = currentActivity.SpanId.ToString();
            if (!string.IsNullOrWhiteSpace(spanId))
            {
                scopeItems.Add(new KeyValuePair<string, object?>("activity.span_id", spanId));
            }
        }

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                scopeItems.Add(pair);
            }
        }

        return scopeItems.Count == 0
            ? Array.Empty<KeyValuePair<string, object?>>()
            : scopeItems;
    }

    private static bool TryGetRequestId(RequestMeta meta, out string requestId)
    {
        foreach (var key in RequestIdHeaderKeys)
        {
            if (meta.Headers.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                requestId = value;
                return true;
            }
        }

        requestId = string.Empty;
        return false;
    }

    private static bool TryGetPeer(RequestMeta meta, Activity? activity, out string? peer, out int? port)
    {
        peer = null;
        port = null;

        if (meta.Headers.TryGetValue("rpc.peer", out var headerPeer) && !string.IsNullOrWhiteSpace(headerPeer))
        {
            peer = headerPeer;
        }

        if (activity is not null)
        {
            peer ??= activity.GetTagItem("net.peer.name") as string ?? activity.GetTagItem("net.peer.ip") as string;

            var portTag = activity.GetTagItem("net.peer.port");
            switch (portTag)
            {
                case int intPort:
                    port ??= intPort;
                    break;
                case string stringPort when int.TryParse(stringPort, out var parsed):
                    port ??= parsed;
                    break;
            }
        }

        return peer is not null || port.HasValue;
    }
}
