using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Microsoft.Extensions.Logging;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Middleware;

public sealed class RpcLoggingMiddleware(ILogger<RpcLoggingMiddleware> logger, RpcLoggingOptions? options = null) :
    IUnaryInboundMiddleware,
    IUnaryOutboundMiddleware,
    IOnewayInboundMiddleware,
    IOnewayOutboundMiddleware,
    IStreamInboundMiddleware,
    IStreamOutboundMiddleware,
    IClientStreamInboundMiddleware,
    IClientStreamOutboundMiddleware,
    IDuplexInboundMiddleware,
    IDuplexOutboundMiddleware
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly RpcLoggingOptions _options = options ?? new RpcLoggingOptions();

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next) =>
        ExecuteWithLogging(
            "inbound unary",
            request.Meta,
            token => next(request, token),
            cancellationToken,
            static response => response.Meta);

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next) =>
        ExecuteWithLogging(
            "outbound unary",
            request.Meta,
            token => next(request, token),
            cancellationToken,
            static response => response.Meta);

    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next) =>
        ExecuteWithLogging(
            "inbound oneway",
            request.Meta,
            token => next(request, token),
            cancellationToken,
            static ack => ack.Meta);

    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundDelegate next) =>
        ExecuteWithLogging(
            "outbound oneway",
            request.Meta,
            token => next(request, token),
            cancellationToken,
            static ack => ack.Meta);

    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundDelegate next) =>
        ExecuteWithLogging(
            $"inbound stream ({options.Direction})",
            request.Meta,
            token => next(request, options, token),
            cancellationToken);

    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundDelegate next) =>
        ExecuteWithLogging(
            $"outbound stream ({options.Direction})",
            request.Meta,
            token => next(request, options, token),
            cancellationToken);

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next) =>
        ExecuteWithLogging(
            "inbound client-stream",
            context.Meta,
            token => next(context, token),
            cancellationToken,
            static response => response.Meta);

    public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next) =>
        ExecuteWithLogging(
            "outbound client-stream",
            requestMeta,
            token => next(requestMeta, token),
            cancellationToken);

    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundDelegate next) =>
        ExecuteWithLogging(
            "inbound duplex",
            request.Meta,
            token => next(request, token),
            cancellationToken);

    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundDelegate next) =>
        ExecuteWithLogging(
            "outbound duplex",
            request.Meta,
            token => next(request, token),
            cancellationToken);

    private async ValueTask<Result<TResponse>> ExecuteWithLogging<TResponse>(
        string pipeline,
        RequestMeta meta,
        Func<CancellationToken, ValueTask<Result<TResponse>>> invoke,
        CancellationToken cancellationToken,
        Func<TResponse, ResponseMeta>? responseMetaAccessor = null)
    {
        var shouldLog = ShouldLog(meta);
        var start = Stopwatch.GetTimestamp();

        try
        {
            var result = await invoke(cancellationToken).ConfigureAwait(false);
            var duration = Stopwatch.GetElapsedTime(start);

            if (result.IsSuccess)
            {
                if (shouldLog)
                {
                    var responseMeta = responseMetaAccessor is null ? null : responseMetaAccessor(result.Value);
                    LogSuccess(pipeline, meta, duration, responseMeta);
                }
            }
            else if (ShouldLogFailure(result.Error!, shouldLog))
            {
                LogFailure(pipeline, meta, duration, result.Error!);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (!shouldLog && !_logger.IsEnabled(_options.FailureLogLevel))
            {
                throw;
            }

            var duration = Stopwatch.GetElapsedTime(start);
            LogException(pipeline, meta, duration, ex);
            throw;
        }
    }

    private bool ShouldLog(RequestMeta meta) =>
        _options.ShouldLogRequest?.Invoke(meta) ?? true;

    private bool ShouldLogFailure(Error error, bool loggedRequest) =>
        _options.ShouldLogError?.Invoke(error) ?? loggedRequest;

    private void LogSuccess(string pipeline, RequestMeta meta, TimeSpan duration, ResponseMeta? responseMeta)
    {
        if (!_logger.IsEnabled(_options.SuccessLogLevel))
        {
            return;
        }

        using var scope = BeginScope(meta, responseMeta);

        _logger.Log(
            _options.SuccessLogLevel,
            "rpc {Pipeline} completed in {DurationMs:F2} ms (service={Service} procedure={Procedure} transport={Transport} caller={Caller} encoding={Encoding} responseEncoding={ResponseEncoding})",
            pipeline,
            duration.TotalMilliseconds,
            meta.Service,
            meta.Procedure ?? string.Empty,
            meta.Transport ?? "unknown",
            meta.Caller ?? string.Empty,
            meta.Encoding ?? "unknown",
            responseMeta?.Encoding ?? "unknown");
    }

    private void LogFailure(string pipeline, RequestMeta meta, TimeSpan duration, Error error)
    {
        if (!_logger.IsEnabled(_options.FailureLogLevel))
        {
            return;
        }

        var status = PolymerErrorAdapter.ToStatus(error);

        using var scope = BeginScope(meta, null);

        _logger.Log(
            _options.FailureLogLevel,
            "rpc {Pipeline} failed in {DurationMs:F2} ms (service={Service} procedure={Procedure} transport={Transport} status={Status} code={Code}) - {ErrorMessage}",
            pipeline,
            duration.TotalMilliseconds,
            meta.Service,
            meta.Procedure ?? string.Empty,
            meta.Transport ?? "unknown",
            status,
            error.Code ?? string.Empty,
            error.Message ?? "unknown failure");
    }

    private void LogException(string pipeline, RequestMeta meta, TimeSpan duration, Exception exception)
    {
        if (!_logger.IsEnabled(_options.FailureLogLevel))
        {
            return;
        }

        using var scope = BeginScope(meta, null);

        _logger.Log(
            _options.FailureLogLevel,
            exception,
            "rpc {Pipeline} threw in {DurationMs:F2} ms (service={Service} procedure={Procedure} transport={Transport})",
            pipeline,
            duration.TotalMilliseconds,
            meta.Service,
            meta.Procedure ?? string.Empty,
            meta.Transport ?? "unknown");
    }

    private IDisposable? BeginScope(RequestMeta meta, ResponseMeta? responseMeta)
    {
        var activity = Activity.Current;
        var scopeItems = new List<KeyValuePair<string, object?>>(10)
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

        if (TryGetPeer(meta, activity, out var peerValue, out var peerPort))
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

        if (activity is not null)
        {
            var traceId = activity.TraceId.ToString();
            if (!string.IsNullOrWhiteSpace(traceId))
            {
                scopeItems.Add(new KeyValuePair<string, object?>("activity.trace_id", traceId));
            }

            var spanId = activity.SpanId.ToString();
            if (!string.IsNullOrWhiteSpace(spanId))
            {
                scopeItems.Add(new KeyValuePair<string, object?>("activity.span_id", spanId));
            }
        }

        if (_options.Enrich is { } enricher)
        {
            var extra = enricher(meta, responseMeta, activity);
            if (extra is not null)
            {
                foreach (var pair in extra)
                {
                    scopeItems.Add(pair);
                }
            }
        }

        return scopeItems.Count == 0 ? null : _logger.BeginScope(scopeItems);
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

    private static readonly string[] RequestIdHeaderKeys =
    {
        "x-request-id",
        "request-id",
        "rpc-request-id"
    };
}
