using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Catches unhandled exceptions in RPC pipelines and converts them to OmniRelay errors, with optional logging.
/// </summary>
public sealed class PanicRecoveryMiddleware(ILogger<PanicRecoveryMiddleware>? logger = null) :
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
    private readonly ILogger? _logger = logger;

    /// <inheritdoc />
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<Response<ReadOnlyMemory<byte>>>(ex, request.Meta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<Response<ReadOnlyMemory<byte>>>(ex, request.Meta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<OnewayAck>(ex, request.Meta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<OnewayAck>(ex, request.Meta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(request, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IStreamCall>(ex, request.Meta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(request, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IStreamCall>(ex, request.Meta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundHandler next)
    {
        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<Response<ReadOnlyMemory<byte>>>(ex, context.Meta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundHandler next)
    {
        requestMeta = EnsureNotNull(requestMeta, nameof(requestMeta));
        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(requestMeta, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IClientStreamTransportCall>(ex, requestMeta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IDuplexStreamCall>(ex, request.Meta);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IDuplexStreamCall>(ex, request.Meta);
        }
    }

    private Result<T> HandleException<T>(Exception exception, RequestMeta? meta)
    {
        if (_logger is not null)
        {
            PanicRecoveryMiddlewareLog.UnhandledException(_logger, meta?.Service ?? string.Empty, meta?.Procedure ?? string.Empty, exception);
        }

        var transport = meta?.Transport ?? "unknown";
        var message = exception.Message ?? "Unhandled exception.";
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, message, transport: transport)
            .WithMetadata("exception_type", exception.GetType().FullName ?? exception.GetType().Name);

        return Err<T>(error);
    }

    private static T EnsureNotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }
}

internal static partial class PanicRecoveryMiddlewareLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unhandled exception in RPC pipeline (service={Service}, procedure={Procedure})")]
    public static partial void UnhandledException(ILogger logger, string service, string procedure, Exception exception);
}
