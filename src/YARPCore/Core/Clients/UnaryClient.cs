using Hugo;
using YARPCore.Core.Middleware;
using YARPCore.Core.Transport;
using static Hugo.Go;

namespace YARPCore.Core.Clients;

public sealed class UnaryClient<TRequest, TResponse>
{
    private readonly UnaryOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    public UnaryClient(IUnaryOutbound outbound, ICodec<TRequest, TResponse> codec, IReadOnlyList<IUnaryOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        var terminal = new UnaryOutboundDelegate(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeUnaryOutbound(middleware, terminal);
    }

    public async ValueTask<Result<Response<TResponse>>> CallAsync(Request<TRequest> request, CancellationToken cancellationToken = default)
    {
        var meta = EnsureEncoding(request.Meta);

        var encodeResult = _codec.EncodeRequest(request.Body, meta);
        if (encodeResult.IsFailure)
        {
            return Err<Response<TResponse>>(encodeResult.Error!);
        }

        var rawRequest = new Request<ReadOnlyMemory<byte>>(meta, encodeResult.Value);
        var outboundResult = await _pipeline(rawRequest, cancellationToken).ConfigureAwait(false);

        if (outboundResult.IsFailure)
        {
            return Err<Response<TResponse>>(outboundResult.Error!);
        }

        var decodeResult = _codec.DecodeResponse(outboundResult.Value.Body, outboundResult.Value.Meta);
        if (decodeResult.IsFailure)
        {
            return Err<Response<TResponse>>(decodeResult.Error!);
        }

        var response = Response<TResponse>.Create(decodeResult.Value, outboundResult.Value.Meta);
        return Ok(response);
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
