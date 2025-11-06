using System.Diagnostics.Metrics;
using Grpc.Core;
using OmniRelay.Core;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Internal metrics for gRPC transport covering client/server unary and streaming operations,
/// including durations, message counts, and client protocol fallbacks.
/// </summary>
internal static class GrpcTransportMetrics
{
    public const string MeterName = "OmniRelay.Transport.Grpc";
    private static readonly Meter Meter = new(MeterName);

    public static readonly Histogram<double> ClientUnaryDuration =
        Meter.CreateHistogram<double>("yarpcore.grpc.client.unary.duration", unit: "ms", description: "Duration of gRPC client unary calls.");

    public static readonly Histogram<double> ServerUnaryDuration =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.unary.duration", unit: "ms", description: "Duration of gRPC server unary calls.");

    public static readonly Histogram<double> ClientServerStreamDuration =
        Meter.CreateHistogram<double>("yarpcore.grpc.client.server_stream.duration", unit: "ms", description: "Duration of gRPC client server-stream calls.");

    public static readonly Histogram<double> ClientServerStreamResponseCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.client.server_stream.responses", description: "Response message count for gRPC client server-stream calls.");

    public static readonly Counter<long> ClientServerStreamResponseMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.client.server_stream.response_messages", description: "Total response messages observed by client server-stream calls.");

    public static readonly Histogram<double> ClientClientStreamDuration =
        Meter.CreateHistogram<double>("yarpcore.grpc.client.client_stream.duration", unit: "ms", description: "Duration of gRPC client streaming calls.");

    public static readonly Histogram<double> ClientClientStreamRequestCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.client.client_stream.requests", description: "Request message count for gRPC client streaming calls.");

    public static readonly Counter<long> ClientClientStreamRequestMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.client.client_stream.request_messages", description: "Total request messages sent by gRPC client streaming calls.");

    public static readonly Histogram<double> ClientDuplexDuration =
        Meter.CreateHistogram<double>("yarpcore.grpc.client.duplex.duration", unit: "ms", description: "Duration of gRPC client duplex calls.");

    public static readonly Histogram<double> ClientDuplexRequestCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.client.duplex.requests", description: "Request message count for gRPC client duplex calls.");

    public static readonly Histogram<double> ClientDuplexResponseCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.client.duplex.responses", description: "Response message count for gRPC client duplex calls.");

    public static readonly Counter<long> ClientDuplexRequestMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.client.duplex.request_messages", description: "Total request messages sent by gRPC client duplex calls.");

    public static readonly Counter<long> ClientDuplexResponseMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.client.duplex.response_messages", description: "Total response messages received by gRPC client duplex calls.");

    public static readonly Histogram<double> ServerServerStreamDuration =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.server_stream.duration", unit: "ms", description: "Duration of gRPC server server-stream handlers.");

    public static readonly Histogram<double> ServerServerStreamResponseCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.server_stream.responses", description: "Response message count for gRPC server server-stream handlers.");

    public static readonly Counter<long> ServerServerStreamResponseMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.server.server_stream.response_messages", description: "Total response messages emitted by gRPC server server-stream handlers.");

    public static readonly Histogram<double> ServerClientStreamDuration =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.client_stream.duration", unit: "ms", description: "Duration of gRPC server client-stream handlers.");

    public static readonly Histogram<double> ServerClientStreamRequestCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.client_stream.requests", description: "Request message count for gRPC server client-stream handlers.");

    public static readonly Counter<long> ServerClientStreamRequestMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.server.client_stream.request_messages", description: "Total request messages received by gRPC server client-stream handlers.");

    public static readonly Histogram<double> ServerClientStreamResponseCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.client_stream.responses", description: "Response message count for gRPC server client-stream handlers.");

    public static readonly Counter<long> ServerClientStreamResponseMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.server.client_stream.response_messages", description: "Total response messages emitted by gRPC server client-stream handlers.");

    public static readonly Histogram<double> ServerDuplexDuration =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.duplex.duration", unit: "ms", description: "Duration of gRPC server duplex handlers.");

    public static readonly Histogram<double> ServerDuplexRequestCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.duplex.requests", description: "Request message count for gRPC server duplex handlers.");

    public static readonly Histogram<double> ServerDuplexResponseCount =
        Meter.CreateHistogram<double>("yarpcore.grpc.server.duplex.responses", description: "Response message count for gRPC server duplex handlers.");

    public static readonly Counter<long> ServerDuplexRequestMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.server.duplex.request_messages", description: "Total request messages received by gRPC server duplex handlers.");

    public static readonly Counter<long> ServerDuplexResponseMessages =
        Meter.CreateCounter<long>("yarpcore.grpc.server.duplex.response_messages", description: "Total response messages emitted by gRPC server duplex handlers.");

    // Protocol fallback tracking when HTTP/3 is enabled but we selected a non-H3 endpoint
    public static readonly Counter<long> ClientProtocolFallbacks =
        Meter.CreateCounter<long>("omnirelay.grpc.client.fallbacks", description: "gRPC client fallbacks when HTTP/3 is enabled but a non-H3 endpoint is selected.");

    /// <summary>
    /// Creates common metric tags for gRPC calls using request metadata.
    /// </summary>
    public static KeyValuePair<string, object?>[] CreateBaseTags(RequestMeta meta)
    {
        List<KeyValuePair<string, object?>> tags;

        if (meta is null)
        {
            tags =
            [
                KeyValuePair.Create<string, object?>("rpc.system", "grpc"),
                KeyValuePair.Create<string, object?>("rpc.service", string.Empty),
                KeyValuePair.Create<string, object?>("rpc.method", string.Empty)
            ];
        }
        else
        {
            tags =
            [
                KeyValuePair.Create<string, object?>("rpc.system", "grpc"),
                KeyValuePair.Create<string, object?>("rpc.service", meta.Service ?? string.Empty),
                KeyValuePair.Create<string, object?>("rpc.method", meta.Procedure ?? string.Empty)
            ];

            if (meta.Headers.TryGetValue("rpc.protocol", out var protocol) && !string.IsNullOrWhiteSpace(protocol))
            {
                tags.Add(KeyValuePair.Create<string, object?>("rpc.protocol", protocol));

                if (TryParseHttpProtocol(protocol, out var name, out var version))
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        tags.Add(KeyValuePair.Create<string, object?>("network.protocol.name", name));
                    }

                    if (!string.IsNullOrEmpty(version))
                    {
                        tags.Add(KeyValuePair.Create<string, object?>("network.protocol.version", version));
                    }
                }
            }
        }

        return [.. tags];
    }

    /// <summary>
    /// Appends gRPC status code to an existing tag set.
    /// </summary>
    public static KeyValuePair<string, object?>[] AppendStatus(KeyValuePair<string, object?>[] baseTags, StatusCode statusCode)
    {
        var tags = new KeyValuePair<string, object?>[baseTags.Length + 1];
        Array.Copy(baseTags, tags, baseTags.Length);
        tags[^1] = KeyValuePair.Create<string, object?>("rpc.grpc.status_code", statusCode);
        return tags;
    }

    /// <summary>
    /// Records a client protocol fallback when HTTP/3 was desired but not selected.
    /// </summary>
    public static void RecordClientFallback(RequestMeta meta, bool http3Desired)
    {
        if (!http3Desired)
        {
            return;
        }

        var tags = CreateBaseTags(meta);
        ClientProtocolFallbacks.Add(1, tags);
    }

    private static bool TryParseHttpProtocol(string protocol, out string? name, out string? version)
    {
        if (protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            name = "http";
            version = protocol.Length > 5 ? protocol[5..] : null;
            return true;
        }

        name = null;
        version = null;
        return false;
    }
}
