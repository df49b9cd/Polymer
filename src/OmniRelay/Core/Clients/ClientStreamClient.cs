using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;

namespace OmniRelay.Core.Clients;

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
        ArgumentNullException.ThrowIfNull(outbound);

        var terminal = new ClientStreamOutboundDelegate(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeClientStreamOutbound(middleware, terminal);
    }

    public async ValueTask<ClientStreamSession> StartAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var normalizedMeta = EnsureEncoding(meta);

        var result = await _pipeline(normalizedMeta, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw OmniRelayErrors.FromError(result.Error!, normalizedMeta.Transport ?? "unknown");
        }

        return new ClientStreamSession(normalizedMeta, _codec, result.Value);
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
                throw OmniRelayErrors.FromError(encodeResult.Error!, _meta.Transport ?? "unknown");
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
                throw OmniRelayErrors.FromError(result.Error!, _meta.Transport ?? "unknown");
            }

            var decode = _codec.DecodeResponse(result.Value.Body, result.Value.Meta);
            if (decode.IsFailure)
            {
                throw OmniRelayErrors.FromError(decode.Error!, _meta.Transport ?? "unknown");
            }

            return Response<TResponse>.Create(decode.Value, result.Value.Meta);
        }
    }

    private RequestMeta EnsureEncoding(RequestMeta meta)
    {
        if (string.IsNullOrWhiteSpace(meta.Encoding))
        {
            return meta with { Encoding = _codec.Encoding };
        }

        return meta;
    }
}
