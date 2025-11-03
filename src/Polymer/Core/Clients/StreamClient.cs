using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Clients;

public sealed class StreamClient<TRequest, TResponse>
{
    private readonly StreamOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    public StreamClient(
        IStreamOutbound outbound,
        ICodec<TRequest, TResponse> codec,
        IReadOnlyList<IStreamOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        ArgumentNullException.ThrowIfNull(outbound);

        var terminal = new StreamOutboundDelegate(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeStreamOutbound(middleware, terminal);
    }

    public async IAsyncEnumerable<Response<TResponse>> CallAsync(
        Request<TRequest> request,
        StreamCallOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var encodeResult = _codec.EncodeRequest(request.Body, request.Meta);
        if (encodeResult.IsFailure)
        {
            throw PolymerErrors.FromError(encodeResult.Error!, options.Direction.ToString());
        }

        var rawRequest = new Request<ReadOnlyMemory<byte>>(request.Meta, encodeResult.Value);
        var streamResult = await _pipeline(rawRequest, options, cancellationToken).ConfigureAwait(false);
        if (streamResult.IsFailure)
        {
            throw PolymerErrors.FromError(streamResult.Error!, options.Direction.ToString());
        }

        await using var call = streamResult.Value;

        await foreach (var payload in call.Responses.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var decodeResult = _codec.DecodeResponse(payload, call.ResponseMeta);
            if (decodeResult.IsFailure)
            {
                await call.CompleteAsync(decodeResult.Error!, cancellationToken).ConfigureAwait(false);
                throw PolymerErrors.FromError(decodeResult.Error!, request.Meta.Transport ?? "stream");
            }

            yield return Response<TResponse>.Create(decodeResult.Value, call.ResponseMeta);
        }
    }
}
