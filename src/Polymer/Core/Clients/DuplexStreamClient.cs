using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Clients;

public sealed class DuplexStreamClient<TRequest, TResponse>
{
    private readonly DuplexOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    public DuplexStreamClient(
        IDuplexOutbound outbound,
        ICodec<TRequest, TResponse> codec,
        IReadOnlyList<IDuplexOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        if (outbound is null)
        {
            throw new ArgumentNullException(nameof(outbound));
        }

        _pipeline = MiddlewareComposer.ComposeDuplexOutbound(middleware, outbound.CallAsync);
    }

    public async ValueTask<DuplexStreamSession> StartAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        if (meta is null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var result = await _pipeline(request, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw PolymerErrors.FromError(result.Error!, meta.Transport ?? "unknown");
        }

        return new DuplexStreamSession(meta, _codec, result.Value);
    }

    public sealed class DuplexStreamSession : IAsyncDisposable
    {
        private readonly RequestMeta _meta;
        private readonly ICodec<TRequest, TResponse> _codec;
        private readonly IDuplexStreamCall _call;

        internal DuplexStreamSession(RequestMeta meta, ICodec<TRequest, TResponse> codec, IDuplexStreamCall call)
        {
            _meta = meta;
            _codec = codec;
            _call = call;
        }

        public RequestMeta RequestMeta => _meta;

        public ResponseMeta ResponseMeta => _call.ResponseMeta;

        public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _call.RequestWriter;

        public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _call.ResponseReader;

        public async ValueTask WriteAsync(TRequest message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encodeResult = _codec.EncodeRequest(message, _meta);
            if (encodeResult.IsFailure)
            {
                throw PolymerErrors.FromError(encodeResult.Error!, _meta.Transport ?? "unknown");
            }

            await _call.RequestWriter.WriteAsync(encodeResult.Value, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _call.CompleteRequestsAsync(error, cancellationToken);

        public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _call.CompleteResponsesAsync(error, cancellationToken);

        public async IAsyncEnumerable<Response<TResponse>> ReadResponsesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var payload in _call.ResponseReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var decode = _codec.DecodeResponse(payload, _call.ResponseMeta);
                if (decode.IsFailure)
                {
                    await _call.CompleteResponsesAsync(decode.Error!, cancellationToken).ConfigureAwait(false);
                    throw PolymerErrors.FromError(decode.Error!, _meta.Transport ?? "unknown");
                }

                yield return Response<TResponse>.Create(decode.Value, _call.ResponseMeta);
            }
        }

        public ValueTask DisposeAsync() => _call.DisposeAsync();
    }
}
