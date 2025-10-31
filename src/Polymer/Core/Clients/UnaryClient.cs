using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Clients;

public sealed class UnaryClient<TRequest, TResponse>
{
    private readonly UnaryOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    public UnaryClient(IUnaryOutbound outbound, ICodec<TRequest, TResponse> codec, IReadOnlyList<IUnaryOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        var terminal = new UnaryOutboundDelegate(outbound.CallAsync);
        _pipeline = Middleware.MiddlewareComposer.ComposeUnaryOutbound(middleware, terminal);
    }

    public async ValueTask<Result<Response<TResponse>>> CallAsync(Request<TRequest> request, CancellationToken cancellationToken = default)
    {
        var encodeResult = _codec.EncodeRequest(request.Body, request.Meta);
        if (encodeResult.IsFailure)
        {
            return Err<Response<TResponse>>(encodeResult.Error!);
        }

        var rawRequest = new Request<ReadOnlyMemory<byte>>(request.Meta, encodeResult.Value);
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
}
