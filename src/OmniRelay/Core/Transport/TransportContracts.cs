using Hugo;

namespace OmniRelay.Core.Transport;

/// <summary>Delegate for unary outbound calls.</summary>
public delegate ValueTask<Result<Response<ReadOnlyMemory<byte>>>> UnaryOutboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Delegate for unary inbound handlers.</summary>
public delegate ValueTask<Result<Response<ReadOnlyMemory<byte>>>> UnaryInboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Delegate for oneway outbound calls.</summary>
public delegate ValueTask<Result<OnewayAck>> OnewayOutboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Delegate for oneway inbound handlers.</summary>
public delegate ValueTask<Result<OnewayAck>> OnewayInboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Delegate for streaming outbound calls.</summary>
public delegate ValueTask<Result<IStreamCall>> StreamOutboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    StreamCallOptions options,
    CancellationToken cancellationToken);

/// <summary>Delegate for streaming inbound handlers.</summary>
public delegate ValueTask<Result<IStreamCall>> StreamInboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    StreamCallOptions options,
    CancellationToken cancellationToken);

/// <summary>Delegate for client-stream inbound handlers.</summary>
public delegate ValueTask<Result<Response<ReadOnlyMemory<byte>>>> ClientStreamInboundDelegate(
    ClientStreamRequestContext context,
    CancellationToken cancellationToken);

/// <summary>Delegate for client-stream outbound calls.</summary>
public delegate ValueTask<Result<IClientStreamTransportCall>> ClientStreamOutboundDelegate(
    RequestMeta requestMeta,
    CancellationToken cancellationToken);

/// <summary>Delegate for duplex streaming outbound calls.</summary>
public delegate ValueTask<Result<IDuplexStreamCall>> DuplexOutboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Delegate for duplex streaming inbound handlers.</summary>
public delegate ValueTask<Result<IDuplexStreamCall>> DuplexInboundDelegate(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

public interface IClientStreamTransportCall : IAsyncDisposable
{
    /// <summary>Gets the request metadata.</summary>
    RequestMeta RequestMeta { get; }
    /// <summary>Gets the response metadata.</summary>
    ResponseMeta ResponseMeta { get; }
    /// <summary>Gets the task that completes with the unary response.</summary>
    Task<Result<Response<ReadOnlyMemory<byte>>>> Response { get; }
    /// <summary>Writes a request message to the stream.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    /// <summary>Signals completion of the request stream.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}

public interface IUnaryOutbound : ILifecycle
{
    /// <summary>Performs a unary call.</summary>
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IOnewayOutbound : ILifecycle
{
    /// <summary>Performs a oneway call.</summary>
    ValueTask<Result<OnewayAck>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IStreamOutbound : ILifecycle
{
    /// <summary>Performs a streaming call.</summary>
    ValueTask<Result<IStreamCall>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken = default);
}

public interface IUnaryInbound
{
    /// <summary>Handles a unary request.</summary>
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> HandleAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IOnewayInbound
{
    /// <summary>Handles a oneway request.</summary>
    ValueTask<Result<OnewayAck>> HandleAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IStreamInbound
{
    /// <summary>Handles a streaming request.</summary>
    ValueTask<Result<IStreamCall>> HandleAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken = default);
}

public interface IClientStreamOutbound : ILifecycle
{
    /// <summary>Starts a client-streaming call.</summary>
    ValueTask<Result<IClientStreamTransportCall>> CallAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken = default);
}

public interface IDuplexOutbound : ILifecycle
{
    /// <summary>Starts a duplex-streaming call.</summary>
    ValueTask<Result<IDuplexStreamCall>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IDuplexInbound
{
    /// <summary>Handles a duplex-streaming request.</summary>
    ValueTask<Result<IDuplexStreamCall>> HandleAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default);
}

public interface IOutboundDiagnostic
{
    /// <summary>Gets transport-specific outbound diagnostic information.</summary>
    object? GetOutboundDiagnostics();
}
