using Hugo;
using YARPCore.Core.Middleware;
using YARPCore.Core.Transport;
using static Hugo.Go;

namespace YARPCore.Core.Clients;

public sealed class OnewayClient<TRequest>
{
    private readonly OnewayOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, object> _codec;

    public OnewayClient(
        IOnewayOutbound outbound,
        ICodec<TRequest, object> codec,
        IReadOnlyList<IOnewayOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));

        ArgumentNullException.ThrowIfNull(outbound);

        var terminal = new OnewayOutboundDelegate(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeOnewayOutbound(middleware, terminal);
    }

    public async ValueTask<Result<OnewayAck>> CallAsync(Request<TRequest> request, CancellationToken cancellationToken = default)
    {
        var meta = EnsureEncoding(request.Meta);

        var encodeResult = _codec.EncodeRequest(request.Body, meta);
        if (encodeResult.IsFailure)
        {
            return Err<OnewayAck>(encodeResult.Error!);
        }

        var outboundRequest = new Request<ReadOnlyMemory<byte>>(meta, encodeResult.Value);
        var ackResult = await _pipeline(outboundRequest, cancellationToken).ConfigureAwait(false);
        return ackResult;
    }

    private RequestMeta EnsureEncoding(RequestMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (string.IsNullOrWhiteSpace(meta.Encoding))
        {
            return meta with { Encoding = _codec.Encoding };
        }

        return meta;
    }
}
