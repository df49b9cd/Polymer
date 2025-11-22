using System.Diagnostics.Metrics;

namespace OmniRelay.Transport.Http;

/// <summary>
/// Internal metrics for the HTTP transport including request counts, durations, and client protocol fallbacks.
/// </summary>
internal static class HttpTransportMetrics
{
    public const string MeterName = "OmniRelay.Transport.Http";
    private static readonly Meter Meter = new(MeterName);

    // Generic request metrics (applies to unary and streaming endpoints)
    public static readonly Counter<long> RequestsStarted =
        Meter.CreateCounter<long>("omnirelay.http.requests.started", description: "HTTP requests started by OmniRelay inbound.");

    public static readonly Counter<long> RequestsCompleted =
        Meter.CreateCounter<long>("omnirelay.http.requests.completed", description: "HTTP requests completed by OmniRelay inbound.");

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("omnirelay.http.request.duration", unit: "ms", description: "HTTP request duration in milliseconds measured at OmniRelay inbound.");

    // Client-side: count when HTTP/3 was desired but a lower protocol was used
    public static readonly Counter<long> ClientProtocolFallbacks =
        Meter.CreateCounter<long>("omnirelay.http.client.fallbacks", description: "HTTP client fallbacks when HTTP/3 was desired but a lower protocol was used.");

    /// <summary>
    /// Creates common metric tags for HTTP requests including RPC attributes and protocol details.
    /// </summary>
    /// <param name="service">The RPC service name.</param>
    /// <param name="procedure">The RPC procedure name.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="protocol">The HTTP protocol label, e.g., HTTP/2 or HTTP/3.</param>
    /// <returns>An array of metric tags.</returns>
    public static KeyValuePair<string, object?>[] CreateBaseTags(
        string service,
        string procedure,
        string method,
        string protocol)
    {
        var tags = new List<KeyValuePair<string, object?>>(8)
        {
            KeyValuePair.Create<string, object?>("rpc.system", "http"),
            KeyValuePair.Create<string, object?>("rpc.service", service ?? string.Empty),
            KeyValuePair.Create<string, object?>("rpc.procedure", procedure ?? string.Empty),
            KeyValuePair.Create<string, object?>("http.request.method", method ?? string.Empty)
        };

        if (!string.IsNullOrWhiteSpace(protocol))
        {
            tags.Add(KeyValuePair.Create<string, object?>("rpc.protocol", protocol));

            if (protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(KeyValuePair.Create<string, object?>("network.protocol.name", "http"));
                var version = protocol.Length > 5 ? protocol[5..] : string.Empty;
                if (!string.IsNullOrEmpty(version))
                {
                    tags.Add(KeyValuePair.Create<string, object?>("network.protocol.version", version));
                }

                tags.Add(KeyValuePair.Create<string, object?>("network.transport", version.StartsWith('3') ? "quic" : "tcp"));
            }
        }

        return [.. tags];
    }

    /// <summary>
    /// Adds response outcome tags to a tag set, optionally appending the HTTP status code.
    /// </summary>
    /// <param name="baseTags">The base tag set.</param>
    /// <param name="httpStatus">Optional HTTP status code.</param>
    /// <param name="outcome">Outcome label (e.g., success, error, cancelled).</param>
    /// <returns>A new tag set with outcome tags appended.</returns>
    public static KeyValuePair<string, object?>[] AppendOutcome(
        KeyValuePair<string, object?>[] baseTags,
        int? httpStatus,
        string outcome)
    {
        var size = baseTags.Length + (httpStatus.HasValue ? 2 : 1);
        var tags = new KeyValuePair<string, object?>[size];
        Array.Copy(baseTags, tags, baseTags.Length);
        var index = baseTags.Length;
        if (httpStatus.HasValue)
        {
            tags[index++] = KeyValuePair.Create<string, object?>("http.response.status_code", httpStatus.Value);
        }
        tags[index] = KeyValuePair.Create<string, object?>("outcome", outcome);
        return tags;
    }

    /// <summary>
    /// Adds an observed protocol tag to a tag set for client-side fallback analysis.
    /// </summary>
    /// <param name="baseTags">The base tag set.</param>
    /// <param name="observedProtocol">The actual protocol observed, if known.</param>
    /// <returns>A new tag set with the observed protocol appended.</returns>
    public static KeyValuePair<string, object?>[] AppendObservedProtocol(
        KeyValuePair<string, object?>[] baseTags,
        string? observedProtocol)
    {
        if (string.IsNullOrWhiteSpace(observedProtocol))
        {
            return baseTags;
        }

        var tags = new KeyValuePair<string, object?>[baseTags.Length + 1];
        Array.Copy(baseTags, tags, baseTags.Length);
        tags[^1] = KeyValuePair.Create<string, object?>("http.observed_protocol", observedProtocol);
        return tags;
    }
}
