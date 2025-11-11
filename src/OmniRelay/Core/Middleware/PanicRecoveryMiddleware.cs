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
        UnaryInboundDelegate next)
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
        UnaryOutboundDelegate next)
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
        OnewayInboundDelegate next)
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
        OnewayOutboundDelegate next)
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
        StreamInboundDelegate next)
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
        StreamOutboundDelegate next)
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
        ClientStreamInboundDelegate next)
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
        ClientStreamOutboundDelegate next)
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
        DuplexInboundDelegate next)
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
        DuplexOutboundDelegate next)
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
        _logger?.LogError(exception, "Unhandled exception in RPC pipeline (service={Service}, procedure={Procedure})", meta?.Service, meta?.Procedure);

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

