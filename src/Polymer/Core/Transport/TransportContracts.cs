using System;
using System.Threading;
using System.Threading.Tasks;
using Hugo;

namespace Polymer.Core.Transport;

public delegate ValueTask<Result<Response<ReadOnlyMemory<byte>>>> UnaryOutboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

public delegate ValueTask<Result<Response<ReadOnlyMemory<byte>>>> UnaryInboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

public delegate ValueTask<Result<OnewayAck>> OnewayOutboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

public delegate ValueTask<Result<OnewayAck>> OnewayInboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

public delegate ValueTask<Result<IStreamCall>> StreamOutboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    StreamCallOptions options,
    CancellationToken cancellationToken);

public delegate ValueTask<Result<IStreamCall>> StreamInboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    StreamCallOptions options,
    CancellationToken cancellationToken);

public interface IUnaryOutbound : ILifecycle
{
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IOnewayOutbound : ILifecycle
{
    ValueTask<Result<OnewayAck>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IStreamOutbound : ILifecycle
{
    ValueTask<Result<IStreamCall>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken = default);
}

public interface IUnaryInbound
{
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> HandleAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IOnewayInbound
{
    ValueTask<Result<OnewayAck>> HandleAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IStreamInbound
{
    ValueTask<Result<IStreamCall>> HandleAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken = default);
}
