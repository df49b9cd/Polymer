using System.Globalization;
using System.Net.WebSockets;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Transport.Http;

public sealed class HttpDuplexOutbound(Uri baseAddress) : IDuplexOutbound, IOutboundDiagnostic
{
    private readonly Uri _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public async ValueTask<Result<IDuplexStreamCall>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Meta.Procedure))
            {
                return Err<IDuplexStreamCall>(OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.InvalidArgument,
                    "Procedure metadata is required for HTTP duplex streaming calls.",
                    transport: "http"));
            }

            var socket = new ClientWebSocket();
            ApplyHeaders(socket, request.Meta);
            var webSocketUri = BuildWebSocketUri(_baseAddress);

            await socket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);

            var responseMeta = new ResponseMeta(transport: "http", encoding: request.Meta.Encoding);
            var transportCall = await HttpDuplexStreamTransportCall.CreateAsync(request.Meta, responseMeta, socket, cancellationToken).ConfigureAwait(false);
            return transportCall;
        }
        catch (Exception ex)
        {
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

    public object GetOutboundDiagnostics() =>
        new HttpDuplexOutboundSnapshot(_baseAddress);
}

public sealed record HttpDuplexOutboundSnapshot(Uri BaseAddress);
