using System.Threading.Channels;
using Hugo;
using static Hugo.Go;

namespace OmniRelay.Core.Transport;

/// <summary>Handler signature for unary outbound calls.</summary>
public delegate ValueTask<Result<Response<ReadOnlyMemory<byte>>>> UnaryOutboundHandler(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Handler signature for unary inbound handlers.</summary>
public delegate ValueTask<Result<Response<ReadOnlyMemory<byte>>>> UnaryInboundHandler(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Handler signature for oneway outbound calls.</summary>
public delegate ValueTask<Result<OnewayAck>> OnewayOutboundHandler(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Handler signature for oneway inbound handlers.</summary>
public delegate ValueTask<Result<OnewayAck>> OnewayInboundHandler(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Handler signature for streaming outbound calls.</summary>
public delegate ValueTask<Result<IStreamCall>> StreamOutboundHandler(
    IRequest<ReadOnlyMemory<byte>> request,
    StreamCallOptions options,
    CancellationToken cancellationToken);

/// <summary>Handler signature for streaming inbound handlers.</summary>
public delegate ValueTask<Result<IStreamCall>> StreamInboundHandler(
    IRequest<ReadOnlyMemory<byte>> request,
    StreamCallOptions options,
    CancellationToken cancellationToken);

/// <summary>Handler signature for client-stream inbound handlers.</summary>
public delegate ValueTask<Result<Response<ReadOnlyMemory<byte>>>> ClientStreamInboundHandler(
    ClientStreamRequestContext context,
    CancellationToken cancellationToken);

/// <summary>Handler signature for client-stream outbound calls.</summary>
public delegate ValueTask<Result<IClientStreamTransportCall>> ClientStreamOutboundHandler(
    RequestMeta requestMeta,
    CancellationToken cancellationToken);

/// <summary>Handler signature for duplex streaming outbound calls.</summary>
public delegate ValueTask<Result<IDuplexStreamCall>> DuplexOutboundHandler(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Handler signature for duplex streaming inbound handlers.</summary>
public delegate ValueTask<Result<IDuplexStreamCall>> DuplexInboundHandler(
    IRequest<ReadOnlyMemory<byte>> request,
    CancellationToken cancellationToken);

/// <summary>Result-based client-stream transport call (non-throwing writes/completion).</summary>
public interface IResultClientStreamTransportCall : IAsyncDisposable
{
    RequestMeta RequestMeta { get; }
    ResponseMeta ResponseMeta { get; }
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response { get; }
    ValueTask<Result<Unit>> WriteAsyncResult(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    ValueTask<Result<Unit>> CompleteAsyncResult(CancellationToken cancellationToken = default);
}

/// <summary>Result-based duplex stream call (non-throwing completion).</summary>
public interface IResultDuplexStreamCall : IAsyncDisposable
{
    RequestMeta RequestMeta { get; }
    ResponseMeta ResponseMeta { get; }
    DuplexStreamCallContext Context { get; }
    ChannelWriter<ReadOnlyMemory<byte>> RequestWriter { get; }
    ChannelReader<ReadOnlyMemory<byte>> RequestReader { get; }
    ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter { get; }
    ChannelReader<ReadOnlyMemory<byte>> ResponseReader { get; }
    ValueTask<Result<Unit>> CompleteRequestsResultAsync(Error? fault = null, CancellationToken cancellationToken = default);
    ValueTask<Result<Unit>> CompleteResponsesResultAsync(Error? fault = null, CancellationToken cancellationToken = default);
}

public interface IClientStreamTransportCall : IAsyncDisposable
{
    /// <summary>Gets the request metadata.</summary>
    RequestMeta RequestMeta { get; }
    /// <summary>Gets the response metadata.</summary>
    ResponseMeta ResponseMeta { get; }
    /// <summary>Gets the ValueTask that completes with the unary response.</summary>
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response { get; }
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
