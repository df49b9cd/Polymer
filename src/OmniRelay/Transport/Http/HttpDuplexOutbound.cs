using System.Globalization;
using System.Net.WebSockets;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Http;

/// <summary>
/// HTTP duplex outbound transport that uses WebSockets to perform bidirectional streaming RPCs.
/// </summary>
/// <param name="baseAddress">The HTTP base address that will be converted to a WebSocket endpoint.</param>
public sealed class HttpDuplexOutbound(Uri baseAddress) : IDuplexOutbound, IOutboundDiagnostic
{
    private readonly Uri _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));

    /// <summary>
    /// Starts the duplex outbound transport. No-op for the WebSocket client implementation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Stops the duplex outbound transport.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Initiates a bidirectional streaming RPC over WebSockets.
    /// </summary>
    /// <param name="request">The request metadata and optional initial payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A transport-backed duplex stream call or an error.</returns>
    public ValueTask<Result<IDuplexStreamCall>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        return Result.Ok(request)
            .Ensure(
                req => !string.IsNullOrWhiteSpace(req.Meta.Procedure),
                req => OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.InvalidArgument,
                    "Procedure metadata is required for HTTP duplex streaming calls.",
                    transport: "http"))
            .ThenValueTaskAsync(ConnectAsync, cancellationToken);
    }

    private async ValueTask<Result<IDuplexStreamCall>> ConnectAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();

        try
        {
            ApplyHeaders(socket, request.Meta);
            var webSocketUri = BuildWebSocketUri(_baseAddress);

            await socket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);

            var responseMeta = new ResponseMeta(transport: "http", encoding: request.Meta.Encoding);
            var transportCall = await HttpDuplexStreamTransportCall
                .CreateAsync(request.Meta, responseMeta, socket, cancellationToken)
                .ConfigureAwait(false);

            if (transportCall.IsFailure)
            {
                socket.Dispose();
            }

            return transportCall;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            return OmniRelayErrors.ToResult<IDuplexStreamCall>(ex, transport: "http");
        }
    }

    private static void ApplyHeaders(ClientWebSocket socket, RequestMeta meta)
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void SetHeader(string key, string? value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            {
                return;
            }

            if (applied.Add(key))
            {
                socket.Options.SetRequestHeader(key, value);
            }
        }

        SetHeader(HttpTransportHeaders.Transport, "http");
        SetHeader(HttpTransportHeaders.Procedure, meta.Procedure ?? string.Empty);
        SetHeader(HttpTransportHeaders.Caller, meta.Caller);
        SetHeader(HttpTransportHeaders.Encoding, meta.Encoding);
        SetHeader(HttpTransportHeaders.ShardKey, meta.ShardKey);
        SetHeader(HttpTransportHeaders.RoutingKey, meta.RoutingKey);
        SetHeader(HttpTransportHeaders.RoutingDelegate, meta.RoutingDelegate);

        if (meta.TimeToLive is { } ttl)
        {
            SetHeader(HttpTransportHeaders.TtlMs, ttl.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
        }

        if (meta.Deadline is { } deadline)
        {
            SetHeader(HttpTransportHeaders.Deadline, deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        foreach (var header in meta.Headers)
        {
            SetHeader(header.Key, header.Value);
        }
    }

    private static Uri BuildWebSocketUri(Uri baseAddress)
    {
        var builder = new UriBuilder(baseAddress)
        {
            Scheme = baseAddress.Scheme switch
            {
                "https" => "wss",
                "http" => "ws",
                _ => baseAddress.Scheme
            }
        };

        return builder.Uri;
    }

    /// <summary>
    /// Returns a snapshot of the duplex outbound configuration for diagnostics.
    /// </summary>
    public object GetOutboundDiagnostics() =>
        new HttpDuplexOutboundSnapshot(_baseAddress);
}

/// <summary>
/// Snapshot of the HTTP duplex outbound configuration for diagnostics.
/// </summary>
public sealed record HttpDuplexOutboundSnapshot(Uri BaseAddress)
{
    public Uri BaseAddress { get; init; } = BaseAddress;
}
