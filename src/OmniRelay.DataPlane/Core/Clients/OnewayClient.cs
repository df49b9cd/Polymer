using Hugo;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;

namespace OmniRelay.Core.Clients;

/// <summary>
/// Typed oneway RPC client that applies middleware and uses an <see cref="ICodec{TRequest, TResponse}"/> for request encoding.
/// </summary>
public sealed class OnewayClient<TRequest>
{
    private readonly OnewayOutboundHandler _pipeline;
    private readonly ICodec<TRequest, object> _codec;

    /// <summary>
    /// Creates a oneway client bound to an outbound and codec.
    /// </summary>
    public OnewayClient(
        IOnewayOutbound outbound,
        ICodec<TRequest, object> codec,
        IReadOnlyList<IOnewayOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));

        ArgumentNullException.ThrowIfNull(outbound);

        var terminal = new OnewayOutboundHandler(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeOnewayOutbound(middleware, terminal);
    }

    /// <summary>
    /// Performs a oneway RPC with the typed request and returns the acknowledgement.
    /// </summary>
    public async ValueTask<Result<OnewayAck>> CallAsync(Request<TRequest> request, CancellationToken cancellationToken = default) =>
        await EncodeRequest(request)
            .ThenValueTaskAsync((outboundRequest, token) => _pipeline(outboundRequest, token), cancellationToken)
            .ConfigureAwait(false);

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
