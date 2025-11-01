using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Clients;

public sealed class ClientStreamClient<TRequest, TResponse>
{
    private readonly ClientStreamOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    public ClientStreamClient(
        IClientStreamOutbound outbound,
        ICodec<TRequest, TResponse> codec,
        IReadOnlyList<IClientStreamOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        if (outbound is null)
        {
            throw new ArgumentNullException(nameof(outbound));
        }

        var terminal = new ClientStreamOutboundDelegate(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeClientStreamOutbound(middleware, terminal);
    }

    public async ValueTask<ClientStreamSession> StartAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        if (meta is null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

        var result = await _pipeline(meta, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw PolymerErrors.FromError(result.Error!, meta.Transport ?? "unknown");
        }

        return new ClientStreamSession(meta, _codec, result.Value);
    }

    public sealed class ClientStreamSession : IAsyncDisposable
    {
        private readonly RequestMeta _meta;
        private readonly ICodec<TRequest, TResponse> _codec;
        private readonly IClientStreamTransportCall _transportCall;
        private readonly Lazy<Task<Response<TResponse>>> _response;

        internal ClientStreamSession(
            RequestMeta meta,
            ICodec<TRequest, TResponse> codec,
            IClientStreamTransportCall transportCall)
        {
            _meta = meta;
            _codec = codec;
            _transportCall = transportCall;
            _response = new Lazy<Task<Response<TResponse>>>(AwaitResponseAsync);
        }

        public RequestMeta RequestMeta => _meta;

        public ResponseMeta ResponseMeta => _transportCall.ResponseMeta;

        public Task<Response<TResponse>> Response => _response.Value;

        public async ValueTask WriteAsync(TRequest message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encodeResult = _codec.EncodeRequest(message, _meta);
            if (encodeResult.IsFailure)
            {
                throw PolymerErrors.FromError(encodeResult.Error!, _meta.Transport ?? "unknown");
            }

            await _transportCall.WriteAsync(encodeResult.Value, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            _transportCall.CompleteAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transportCall.DisposeAsync();

        private async Task<Response<TResponse>> AwaitResponseAsync()
        {
            var result = await _transportCall.Response.ConfigureAwait(false);
            if (result.IsFailure)
            {
                throw PolymerErrors.FromError(result.Error!, _meta.Transport ?? "unknown");
            }

            var decode = _codec.DecodeResponse(result.Value.Body, result.Value.Meta);
            if (decode.IsFailure)
            {
                throw PolymerErrors.FromError(decode.Error!, _meta.Transport ?? "unknown");
            }

            return Response<TResponse>.Create(decode.Value, result.Value.Meta);
        }
    }
}
