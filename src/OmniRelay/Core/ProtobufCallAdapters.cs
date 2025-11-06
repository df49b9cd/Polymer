using System.Runtime.CompilerServices;
using Google.Protobuf;
using Hugo;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core;

/// <summary>
/// Factory helpers for adapting typed Protobuf handlers to OmniRelay transport delegates for all RPC shapes.
/// </summary>
public static class ProtobufCallAdapters
{
    /// <summary>
    /// Creates a unary inbound handler that decodes the request with the provided codec and encodes the response.
    /// </summary>
    public static UnaryInboundDelegate CreateUnaryHandler<TRequest, TResponse>(
        ProtobufCodec<TRequest, TResponse> codec,
        Func<Request<TRequest>, CancellationToken, ValueTask<Response<TResponse>>> handler)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handler);

        return async (request, cancellationToken) =>
        {
            var decode = codec.DecodeRequest(request.Body, request.Meta);
            if (decode.IsFailure)
            {
                return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
            }

            Response<TResponse> response;

            try
            {
                var typedRequest = new Request<TRequest>(request.Meta, decode.Value);
                response = await handler(typedRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OmniRelayErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, request.Meta.Transport ?? "grpc");
            }

            var responseMeta = EnsureResponseMeta(response.Meta, codec.Encoding);
            var encode = codec.EncodeResponse(response.Body, responseMeta);
            if (encode.IsFailure)
            {
                return Err<Response<ReadOnlyMemory<byte>>>(encode.Error!);
            }

            return Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta));
        };
    }

    /// <summary>
    /// Creates a server-stream inbound handler that decodes requests and provides a typed writer.
    /// </summary>
    public static StreamInboundDelegate CreateServerStreamHandler<TRequest, TResponse>(
        ProtobufCodec<TRequest, TResponse> codec,
        Func<Request<TRequest>, ProtobufServerStreamWriter<TRequest, TResponse>, CancellationToken, ValueTask> handler)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handler);

        return (request, _, cancellationToken) => HandleServerStreamAsync(request, codec, handler, cancellationToken);
    }

    /// <summary>
    /// Creates a client-stream inbound handler that exposes a typed reader and encodes the single response.
    /// </summary>
    public static ClientStreamInboundDelegate CreateClientStreamHandler<TRequest, TResponse>(
        ProtobufCodec<TRequest, TResponse> codec,
        Func<ProtobufClientStreamContext<TRequest, TResponse>, CancellationToken, ValueTask<Response<TResponse>>> handler)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handler);

        return async (context, cancellationToken) =>
        {
            var streamContext = new ProtobufClientStreamContext<TRequest, TResponse>(codec, context);
            Response<TResponse> response;

            try
            {
                response = await handler(streamContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OmniRelayErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, context.Meta.Transport ?? "stream");
            }

            var responseMeta = EnsureResponseMeta(response.Meta, codec.Encoding);
            var encode = codec.EncodeResponse(response.Body, responseMeta);
            if (encode.IsFailure)
            {
                return Err<Response<ReadOnlyMemory<byte>>>(encode.Error!);
            }

            return Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta));
        };
    }

    /// <summary>
    /// Creates a duplex-stream inbound handler that exposes typed read/write operations over a duplex call.
    /// </summary>
    public static DuplexInboundDelegate CreateDuplexHandler<TRequest, TResponse>(
        ProtobufCodec<TRequest, TResponse> codec,
        Func<ProtobufDuplexStreamContext<TRequest, TResponse>, CancellationToken, ValueTask> handler)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handler);

        return (request, cancellationToken) => HandleDuplexAsync(request, codec, handler, cancellationToken);
    }

    private static async ValueTask<Result<IStreamCall>> HandleServerStreamAsync<TRequest, TResponse>(
        IRequest<ReadOnlyMemory<byte>> request,
        ProtobufCodec<TRequest, TResponse> codec,
        Func<Request<TRequest>, ProtobufServerStreamWriter<TRequest, TResponse>, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        var decode = codec.DecodeRequest(request.Body, request.Meta);
        if (decode.IsFailure)
        {
            return Err<IStreamCall>(decode.Error!);
        }

        var call = ServerStreamCall.Create(request.Meta, new ResponseMeta(encoding: codec.Encoding));
        var writer = new ProtobufServerStreamWriter<TRequest, TResponse>(codec, call, request.Meta.Transport ?? "stream");
        var typedRequest = new Request<TRequest>(request.Meta, decode.Value);

        _ = RunServerStreamAsync(typedRequest, writer, handler, cancellationToken);

        return Ok((IStreamCall)call);
    }

    private static async Task RunServerStreamAsync<TRequest, TResponse>(
        Request<TRequest> request,
        ProtobufServerStreamWriter<TRequest, TResponse> writer,
        Func<Request<TRequest>, ProtobufServerStreamWriter<TRequest, TResponse>, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        try
        {
            await handler(request, writer, cancellationToken).ConfigureAwait(false);
            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writer.FailAsync(ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<Result<IDuplexStreamCall>> HandleDuplexAsync<TRequest, TResponse>(
        IRequest<ReadOnlyMemory<byte>> request,
        ProtobufCodec<TRequest, TResponse> codec,
        Func<ProtobufDuplexStreamContext<TRequest, TResponse>, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: codec.Encoding));
        var context = new ProtobufDuplexStreamContext<TRequest, TResponse>(codec, call, request.Meta.Transport ?? "stream");

        _ = RunDuplexAsync(context, handler, cancellationToken);

        return Ok((IDuplexStreamCall)call);
    }

    private static async Task RunDuplexAsync<TRequest, TResponse>(
        ProtobufDuplexStreamContext<TRequest, TResponse> context,
        Func<ProtobufDuplexStreamContext<TRequest, TResponse>, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        try
        {
            await handler(context, cancellationToken).ConfigureAwait(false);
            await context.CompleteResponsesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await context.FailAsync(ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ResponseMeta EnsureResponseMeta(ResponseMeta meta, string encoding)
    {
        if (string.IsNullOrWhiteSpace(meta.Encoding))
        {
            return meta with { Encoding = encoding };
        }

        return meta;
    }

    /// <summary>
    /// Typed server-stream writer that encodes responses using the configured codec and updates response metadata.
    /// </summary>
    public sealed class ProtobufServerStreamWriter<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        private readonly ProtobufCodec<TRequest, TResponse> _codec;
        private readonly ServerStreamCall _call;
        private readonly string _transport;
        private ResponseMeta _responseMeta;

        internal ProtobufServerStreamWriter(ProtobufCodec<TRequest, TResponse> codec, ServerStreamCall call, string transport)
        {
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _call = call ?? throw new ArgumentNullException(nameof(call));
            _transport = transport;
            _responseMeta = new ResponseMeta(encoding: codec.Encoding);
            _call.SetResponseMeta(_responseMeta);
        }

        /// <summary>Gets or sets the response metadata propagated to the client.</summary>
        public ResponseMeta ResponseMeta
        {
            get => _responseMeta;
            set
            {
                _responseMeta = EnsureResponseMeta(value ?? new ResponseMeta(), _codec.Encoding);
                _call.SetResponseMeta(_responseMeta);
            }
        }

        /// <summary>Encodes and writes a typed response message.</summary>
        public async ValueTask WriteAsync(TResponse message, CancellationToken cancellationToken = default)
        {
            var encode = _codec.EncodeResponse(message, _responseMeta);
            if (encode.IsFailure)
            {
                await _call.CompleteAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                throw OmniRelayErrors.FromError(encode.Error!, _transport);
            }

            await _call.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Completes the response stream successfully.</summary>
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            _call.CompleteAsync(cancellationToken: cancellationToken);

        internal async ValueTask FailAsync(Exception exception, CancellationToken cancellationToken)
        {
            var failure = OmniRelayErrors.ToResult<Response<ReadOnlyMemory<byte>>>(exception, _transport);
            await _call.CompleteAsync(failure.Error, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Provides typed access to client-stream requests using a codec for decoding.
    /// </summary>
    public sealed class ProtobufClientStreamContext<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        private readonly ProtobufCodec<TRequest, TResponse> _codec;
        private readonly ClientStreamRequestContext _context;

        internal ProtobufClientStreamContext(
            ProtobufCodec<TRequest, TResponse> codec,
            ClientStreamRequestContext context)
        {
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _context = context;
        }

        /// <summary>Gets the request metadata.</summary>
        public RequestMeta Meta => _context.Meta;

        /// <summary>
        /// Iterates and decodes all request messages in the client stream.
        /// </summary>
        public async IAsyncEnumerable<TRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var reader = _context.Requests;
            var transport = Meta.Transport ?? "stream";

            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var payload))
                {
                    var decode = _codec.DecodeRequest(payload, Meta);
                    if (decode.IsFailure)
                    {
                        throw OmniRelayErrors.FromError(decode.Error!, transport);
                    }

                    yield return decode.Value;
                }
            }
        }
    }

    /// <summary>
    /// Provides typed read and write operations for duplex-streaming handlers using a codec.
    /// </summary>
    public sealed class ProtobufDuplexStreamContext<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        private readonly ProtobufCodec<TRequest, TResponse> _codec;
        private readonly DuplexStreamCall _call;
        private readonly string _transport;

        internal ProtobufDuplexStreamContext(
            ProtobufCodec<TRequest, TResponse> codec,
            DuplexStreamCall call,
            string transport)
        {
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _call = call ?? throw new ArgumentNullException(nameof(call));
            _transport = transport;
        }

        /// <summary>Gets the request metadata.</summary>
        public RequestMeta RequestMeta => _call.RequestMeta;

        /// <summary>Gets or sets the response metadata propagated to the client.</summary>
        public ResponseMeta ResponseMeta
        {
            get => _call.ResponseMeta;
            set
            {
                var meta = EnsureResponseMeta(value ?? new ResponseMeta(), _codec.Encoding);
                _call.SetResponseMeta(meta);
            }
        }

        /// <summary>
        /// Iterates and decodes all request messages from the duplex request stream.
        /// </summary>
        public async IAsyncEnumerable<TRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var reader = _call.RequestReader;

            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var payload))
                {
                    var decode = _codec.DecodeRequest(payload, _call.RequestMeta);
                    if (decode.IsFailure)
                    {
                        throw OmniRelayErrors.FromError(decode.Error!, _transport);
                    }

                    yield return decode.Value;
                }
            }
        }

        /// <summary>Encodes and writes a typed response message to the duplex response stream.</summary>
        public async ValueTask WriteAsync(TResponse message, CancellationToken cancellationToken = default)
        {
            var encode = _codec.EncodeResponse(message, _call.ResponseMeta);
            if (encode.IsFailure)
            {
                await FailAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                throw OmniRelayErrors.FromError(encode.Error!, _transport);
            }

            await _call.ResponseWriter.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Signals completion of response messages.</summary>
        public ValueTask CompleteResponsesAsync(CancellationToken cancellationToken = default) =>
            _call.CompleteResponsesAsync(cancellationToken: cancellationToken);

        internal ValueTask FailAsync(Exception exception, CancellationToken cancellationToken)
        {
            var failure = OmniRelayErrors.ToResult<Response<ReadOnlyMemory<byte>>>(exception, _transport);
            return _call.CompleteResponsesAsync(failure.Error, cancellationToken);
        }

        internal ValueTask FailAsync(Error error, CancellationToken cancellationToken) =>
            _call.CompleteResponsesAsync(error, cancellationToken);
    }
}
