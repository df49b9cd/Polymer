using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;

namespace OmniRelay.Core.Clients;

/// <summary>
/// Typed client-streaming RPC client that applies middleware and uses an <see cref="ICodec{TRequest,TResponse}"/>.
/// </summary>
public sealed class ClientStreamClient<TRequest, TResponse>
{
    private readonly ClientStreamOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    /// <summary>
    /// Creates a client-streaming client bound to an outbound and codec.
    /// </summary>
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

    /// <summary>
    /// Starts a client-streaming session and returns a session wrapper to write messages and await the response.
    /// </summary>
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

    /// <summary>
    /// Represents an active client-streaming session with write and completion operations.
    /// </summary>
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

        /// <summary>Gets the request metadata.</summary>
        public RequestMeta RequestMeta => _meta;

        /// <summary>Gets the response metadata.</summary>
        public ResponseMeta ResponseMeta => _transportCall.ResponseMeta;

        /// <summary>Gets the task that completes with the unary response.</summary>
        public Task<Response<TResponse>> Response => _response.Value;

        /// <summary>Writes a typed request message to the stream.</summary>
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

        /// <summary>Signals completion of the request stream.</summary>
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            _transportCall.CompleteAsync(cancellationToken);

        /// <inheritdoc />
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
