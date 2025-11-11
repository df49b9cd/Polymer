using Hugo;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;

namespace OmniRelay.Core.Clients;

/// <summary>
/// Typed unary RPC client that applies middleware and uses an <see cref="ICodec{TRequest,TResponse}"/>.
/// </summary>
public sealed class UnaryClient<TRequest, TResponse>
{
    private readonly UnaryOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    /// <summary>
    /// Creates a unary client bound to an outbound and codec.
    /// </summary>
    public UnaryClient(IUnaryOutbound outbound, ICodec<TRequest, TResponse> codec, IReadOnlyList<IUnaryOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(middleware);

        var terminal = new UnaryOutboundDelegate(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeUnaryOutbound(middleware, terminal);
    }

    /// <summary>
    /// Performs a unary RPC with the typed request and returns a typed response.
    /// </summary>
    public async ValueTask<Result<Response<TResponse>>> CallAsync(Request<TRequest> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outboundResult = await EncodeRequest(request)
            .ThenValueTaskAsync((raw, token) => _pipeline(raw, token), cancellationToken)
            .ConfigureAwait(false);

        return outboundResult.Then(response => _codec
            .DecodeResponse(response.Body, response.Meta)
            .Map(decoded => Response<TResponse>.Create(decoded, response.Meta)));
    }

    private RequestMeta EnsureEncoding(RequestMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        return string.IsNullOrWhiteSpace(meta.Encoding)
            ? meta with { Encoding = _codec.Encoding }
            : meta;
    }

    private Result<Request<ReadOnlyMemory<byte>>> EncodeRequest(Request<TRequest> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var meta = EnsureEncoding(request.Meta);
        return _codec.EncodeRequest(request.Body, meta)
            .Map(payload => new Request<ReadOnlyMemory<byte>>(meta, payload));
    }
}
