using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using Hugo;
using static Hugo.Go;

namespace Polymer.Transport.Http;

public sealed class HttpDuplexOutbound : IDuplexOutbound, IOutboundDiagnostic
{
    private readonly Uri _baseAddress;

    public HttpDuplexOutbound(Uri baseAddress)
    {
        _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
    }

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
                return Err<IDuplexStreamCall>(PolymerErrorAdapter.FromStatus(
                    PolymerStatusCode.InvalidArgument,
                    "Procedure metadata is required for HTTP duplex streaming calls.",
                    transport: "http"));
            }

            var socket = new ClientWebSocket();
            ApplyHeaders(socket, request.Meta);
            var webSocketUri = BuildWebSocketUri(_baseAddress);

            await socket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);

            var responseMeta = new ResponseMeta(transport: "http", encoding: request.Meta.Encoding);
            var transportCall = HttpDuplexStreamTransportCall.Create(request.Meta, responseMeta, socket, cancellationToken);
            return Ok(transportCall);
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<IDuplexStreamCall>(ex, transport: "http");
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

    public object? GetOutboundDiagnostics() =>
        new HttpDuplexOutboundSnapshot(_baseAddress);
}

public sealed record HttpDuplexOutboundSnapshot(Uri BaseAddress);
