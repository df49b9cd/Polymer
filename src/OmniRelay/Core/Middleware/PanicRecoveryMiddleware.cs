using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Middleware;

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

    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next)
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<Response<ReadOnlyMemory<byte>>>(ex, request.Meta);
        }
    }

    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<Response<ReadOnlyMemory<byte>>>(ex, request.Meta);
        }
    }

    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<OnewayAck>(ex, request.Meta);
        }
    }

    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundDelegate next)
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<OnewayAck>(ex, request.Meta);
        }
    }

    public async ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundDelegate next)
    {
        try
        {
            return await next(request, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IStreamCall>(ex, request.Meta);
        }
    }

    public async ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundDelegate next)
    {
        try
        {
            return await next(request, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IStreamCall>(ex, request.Meta);
        }
    }

    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next)
    {
        try
        {
            return await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<Response<ReadOnlyMemory<byte>>>(ex, context.Meta);
        }
    }

    public async ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next)
    {
        try
        {
            return await next(requestMeta, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IClientStreamTransportCall>(ex, requestMeta);
        }
    }

    public async ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundDelegate next)
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HandleException<IDuplexStreamCall>(ex, request.Meta);
        }
    }

    public async ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundDelegate next)
    {
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
}
