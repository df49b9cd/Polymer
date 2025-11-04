using System.Runtime.CompilerServices;
using Google.Protobuf;
using Hugo;
using YARPCore.Core.Transport;
using YARPCore.Errors;
using static Hugo.Go;

namespace YARPCore.Core;

public static class ProtobufCallAdapters
{
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
                return PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, request.Meta.Transport ?? "grpc");
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
                return PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, context.Meta.Transport ?? "stream");
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

        public ResponseMeta ResponseMeta
        {
            get => _responseMeta;
            set
            {
                _responseMeta = EnsureResponseMeta(value ?? new ResponseMeta(), _codec.Encoding);
                _call.SetResponseMeta(_responseMeta);
            }
        }

        public async ValueTask WriteAsync(TResponse message, CancellationToken cancellationToken = default)
        {
            var encode = _codec.EncodeResponse(message, _responseMeta);
            if (encode.IsFailure)
            {
                await _call.CompleteAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                throw PolymerErrors.FromError(encode.Error!, _transport);
            }

            await _call.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            _call.CompleteAsync(cancellationToken: cancellationToken);

        internal async ValueTask FailAsync(Exception exception, CancellationToken cancellationToken)
        {
            var failure = PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(exception, _transport);
            await _call.CompleteAsync(failure.Error, cancellationToken).ConfigureAwait(false);
        }
    }

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

        public RequestMeta Meta => _context.Meta;

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
                        throw PolymerErrors.FromError(decode.Error!, transport);
                    }

                    yield return decode.Value;
                }
            }
        }
    }

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

        public RequestMeta RequestMeta => _call.RequestMeta;

        public ResponseMeta ResponseMeta
        {
            get => _call.ResponseMeta;
            set
            {
                var meta = EnsureResponseMeta(value ?? new ResponseMeta(), _codec.Encoding);
                _call.SetResponseMeta(meta);
            }
        }

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
                        throw PolymerErrors.FromError(decode.Error!, _transport);
                    }

                    yield return decode.Value;
                }
            }
        }

        public async ValueTask WriteAsync(TResponse message, CancellationToken cancellationToken = default)
        {
            var encode = _codec.EncodeResponse(message, _call.ResponseMeta);
            if (encode.IsFailure)
            {
                await FailAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                throw PolymerErrors.FromError(encode.Error!, _transport);
            }

            await _call.ResponseWriter.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask CompleteResponsesAsync(CancellationToken cancellationToken = default) =>
            _call.CompleteResponsesAsync(cancellationToken: cancellationToken);

        internal ValueTask FailAsync(Exception exception, CancellationToken cancellationToken)
        {
            var failure = PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(exception, _transport);
            return _call.CompleteResponsesAsync(failure.Error, cancellationToken);
        }

        internal ValueTask FailAsync(Error error, CancellationToken cancellationToken) =>
            _call.CompleteResponsesAsync(error, cancellationToken);
    }
}
