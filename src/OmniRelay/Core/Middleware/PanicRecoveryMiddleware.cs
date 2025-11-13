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
        UnaryInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(request, cancellationToken).ConfigureAwait(false);
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
        UnaryOutboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(request, cancellationToken).ConfigureAwait(false);
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
        OnewayInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(request, cancellationToken).ConfigureAwait(false);
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
        OnewayOutboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(request, cancellationToken).ConfigureAwait(false);
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
        StreamInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(request, options, cancellationToken).ConfigureAwait(false);
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
        StreamOutboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(request, options, cancellationToken).ConfigureAwait(false);
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
        ClientStreamInboundHandler nextHandler)
    {
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(context, cancellationToken).ConfigureAwait(false);
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
        ClientStreamOutboundHandler nextHandler)
    {
        requestMeta = EnsureNotNull(requestMeta, nameof(requestMeta));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(requestMeta, cancellationToken).ConfigureAwait(false);
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
        DuplexInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(request, cancellationToken).ConfigureAwait(false);
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
        DuplexOutboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        try
        {
            return await nextHandler(request, cancellationToken).ConfigureAwait(false);
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
