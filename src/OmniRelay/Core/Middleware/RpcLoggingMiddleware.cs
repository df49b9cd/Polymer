using System.Diagnostics;
using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Structured logging middleware for all RPC shapes with scope enrichment and duration logging.
/// </summary>
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

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            "inbound unary",
            request.Meta,
            token => next(request, token),
            cancellationToken,
            static response => response.Meta);
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            "outbound unary",
            request.Meta,
            token => next(request, token),
            cancellationToken,
            static response => response.Meta);
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            "inbound oneway",
            request.Meta,
            token => next(request, token),
            cancellationToken,
            static ack => ack.Meta);
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            "outbound oneway",
            request.Meta,
            token => next(request, token),
            cancellationToken,
            static ack => ack.Meta);
    }

    /// <inheritdoc />
    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            $"inbound stream ({options.Direction})",
            request.Meta,
            token => next(request, options, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            $"outbound stream ({options.Direction})",
            request.Meta,
            token => next(request, options, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next)
    {
        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            "inbound client-stream",
            context.Meta,
            token => next(context, token),
            cancellationToken,
            static response => response.Meta);
    }

    /// <inheritdoc />
    public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next)
    {
        requestMeta = EnsureNotNull(requestMeta, nameof(requestMeta));
        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            "outbound client-stream",
            requestMeta,
            token => next(requestMeta, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            "inbound duplex",
            request.Meta,
            token => next(request, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ExecuteWithLogging(
            "outbound duplex",
            request.Meta,
            token => next(request, token),
            cancellationToken);
    }

    private async ValueTask<Result<TResponse>> ExecuteWithLogging<TResponse>(
        string pipeline,
        RequestMeta meta,
        Func<CancellationToken, ValueTask<Result<TResponse>>> invoke,
        CancellationToken cancellationToken,
        Func<TResponse, ResponseMeta>? responseMetaAccessor = null)
    {
        var activity = Activity.Current;
        var baseExtra = _options.Enrich?.Invoke(meta, null, activity);
        using var requestScope = RequestLoggingScope.Begin(_logger, meta, null, activity, baseExtra);

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
                    LogSuccess(pipeline, meta, duration, responseMeta, activity);
                }
            }
            else if (ShouldLogFailure(result.Error!, shouldLog))
            {
                LogFailure(pipeline, meta, duration, result.Error!, activity);
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
            LogException(pipeline, meta, duration, ex, activity);
            throw;
        }
    }

    private bool ShouldLog(RequestMeta meta) =>
        _options.ShouldLogRequest?.Invoke(meta) ?? true;

    private bool ShouldLogFailure(Error error, bool loggedRequest) =>
        _options.ShouldLogError?.Invoke(error) ?? loggedRequest;

    private void LogSuccess(string pipeline, RequestMeta meta, TimeSpan duration, ResponseMeta? responseMeta, Activity? activity)
    {
        if (!_logger.IsEnabled(_options.SuccessLogLevel))
        {
            return;
        }

        var extra = _options.Enrich?.Invoke(meta, responseMeta, activity);
        using var scope = RequestLoggingScope.Begin(_logger, meta, responseMeta, activity, extra);

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

    private void LogFailure(string pipeline, RequestMeta meta, TimeSpan duration, Error error, Activity? activity)
    {
        if (!_logger.IsEnabled(_options.FailureLogLevel))
        {
            return;
        }

        var status = OmniRelayErrorAdapter.ToStatus(error);

        var extra = _options.Enrich?.Invoke(meta, null, activity);
        using var scope = RequestLoggingScope.Begin(_logger, meta, null, activity, extra);

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

    private void LogException(string pipeline, RequestMeta meta, TimeSpan duration, Exception exception, Activity? activity)
    {
        if (!_logger.IsEnabled(_options.FailureLogLevel))
        {
            return;
        }

        var extra = _options.Enrich?.Invoke(meta, null, activity);
        using var scope = RequestLoggingScope.Begin(_logger, meta, null, activity, extra);

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

    private static T EnsureNotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }
}

