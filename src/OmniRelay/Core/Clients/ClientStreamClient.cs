using Hugo;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Clients;

/// <summary>
/// Typed client-streaming RPC client that applies middleware and uses an <see cref="ICodec{TRequest,TResponse}"/>.
/// </summary>
public sealed class ClientStreamClient<TRequest, TResponse>
{
    private readonly ClientStreamOutboundHandler _pipeline;
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

        var terminal = new ClientStreamOutboundHandler(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeClientStreamOutbound(middleware, terminal);
    }

    /// <summary>
    /// Starts a client-streaming session and returns a result-wrapped session wrapper to write messages and await the response.
    /// </summary>
    public async ValueTask<Result<ClientStreamSession>> StartAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var normalizedMeta = EnsureEncoding(meta);

        var result = await _pipeline(normalizedMeta, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return OmniRelayErrors.ToResult<ClientStreamSession>(result.Error!, normalizedMeta.Transport ?? "unknown");
        }

        return Ok(new ClientStreamSession(normalizedMeta, _codec, result.Value));
    }

    /// <summary>
    /// Represents an active client-streaming session with write and completion operations.
    /// </summary>
    public sealed class ClientStreamSession : IAsyncDisposable
    {
        private readonly ICodec<TRequest, TResponse> _codec;
        private readonly IClientStreamTransportCall _transportCall;
        private readonly Lazy<Task<Result<Response<TResponse>>>> _response;

        internal ClientStreamSession(
            RequestMeta meta,
            ICodec<TRequest, TResponse> codec,
            IClientStreamTransportCall transportCall)
        {
            RequestMeta = meta;
            _codec = codec;
            _transportCall = transportCall;
            _response = new Lazy<Task<Result<Response<TResponse>>>>(AwaitResponseAsync);
        }

        /// <summary>Gets the request metadata.</summary>
        public RequestMeta RequestMeta { get; }

        /// <summary>Gets the response metadata.</summary>
        public ResponseMeta ResponseMeta => _transportCall.ResponseMeta;

        /// <summary>Gets the ValueTask that completes with the result-wrapped unary response.</summary>
        public ValueTask<Result<Response<TResponse>>> Response => new(_response.Value);

        /// <summary>Writes a typed request message to the stream, returning a result for success or failure.</summary>
        public async ValueTask<Result<Unit>> WriteAsync(TRequest message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encodeResult = _codec.EncodeRequest(message, RequestMeta);
            if (encodeResult.IsFailure)
            {
                return OmniRelayErrors.ToResult<Unit>(encodeResult.Error!, RequestMeta.Transport ?? "unknown");
            }

            await _transportCall.WriteAsync(encodeResult.Value, cancellationToken).ConfigureAwait(false);
            return Ok(Unit.Value);
        }

        /// <summary>Signals completion of the request stream.</summary>
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            _transportCall.CompleteAsync(cancellationToken);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _transportCall.DisposeAsync();

        private async Task<Result<Response<TResponse>>> AwaitResponseAsync()
        {
            var result = await _transportCall.Response.ConfigureAwait(false);
            if (result.IsFailure)
            {
                return OmniRelayErrors.ToResult<Response<TResponse>>(result.Error!, RequestMeta.Transport ?? "unknown");
            }

            var decode = _codec.DecodeResponse(result.Value.Body, result.Value.Meta);
            if (decode.IsFailure)
            {
                return OmniRelayErrors.ToResult<Response<TResponse>>(decode.Error!, RequestMeta.Transport ?? "unknown");
            }

            return Ok(Response<TResponse>.Create(decode.Value, result.Value.Meta));
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
