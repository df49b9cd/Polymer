using System;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core.Transport;

namespace Polymer.Core.Middleware;

public interface IUnaryOutboundMiddleware
{
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next);
}

public interface IUnaryInboundMiddleware
{
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next);
}

public interface IOnewayOutboundMiddleware
{
    ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundDelegate next);
}

public interface IOnewayInboundMiddleware
{
    ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next);
}

public interface IStreamOutboundMiddleware
{
    ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundDelegate next);
}

public interface IStreamInboundMiddleware
{
    ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundDelegate next);
}

public interface IClientStreamInboundMiddleware
{
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next);
}

public interface IClientStreamOutboundMiddleware
{
    ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next);
}
