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

public sealed class OnewayClient<TRequest>
{
    private readonly OnewayOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, object> _codec;

    public OnewayClient(
        IOnewayOutbound outbound,
        ICodec<TRequest, object> codec,
        IReadOnlyList<IOnewayOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new System.ArgumentNullException(nameof(codec));

        ArgumentNullException.ThrowIfNull(outbound);

        var terminal = new OnewayOutboundDelegate(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeOnewayOutbound(middleware, terminal);
    }

    public async ValueTask<Result<OnewayAck>> CallAsync(Request<TRequest> request, CancellationToken cancellationToken = default)
    {
        var encodeResult = _codec.EncodeRequest(request.Body, request.Meta);
        if (encodeResult.IsFailure)
        {
            return Err<OnewayAck>(encodeResult.Error!);
        }

        var outboundRequest = new Request<ReadOnlyMemory<byte>>(request.Meta, encodeResult.Value);
        var ackResult = await _pipeline(outboundRequest, cancellationToken).ConfigureAwait(false);
        return ackResult;
    }
}
