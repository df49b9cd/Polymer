using System.Runtime.CompilerServices;
using Hugo;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Clients;

/// <summary>
/// Typed server-streaming RPC client that applies middleware and uses an <see cref="ICodec{TRequest,TResponse}"/>.
/// </summary>
public sealed class StreamClient<TRequest, TResponse>
{
    private readonly StreamOutboundHandler _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    /// <summary>
    /// Creates a server-streaming client bound to an outbound and codec.
    /// </summary>
    public StreamClient(
        IStreamOutbound outbound,
        ICodec<TRequest, TResponse> codec,
        IReadOnlyList<IStreamOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(middleware);

        var terminal = new StreamOutboundHandler(outbound.CallAsync);
        _pipeline = MiddlewareComposer.ComposeStreamOutbound(middleware, terminal);
    }

    /// <summary>
    /// Performs a server-streaming RPC and yields result-wrapped typed responses.
    /// </summary>
    public async IAsyncEnumerable<Result<Response<TResponse>>> CallAsync(
        Request<TRequest> request,
        StreamCallOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var meta = EnsureEncoding(request.Meta);

        var encodeResult = _codec.EncodeRequest(request.Body, meta);
        if (encodeResult.IsFailure)
        {
            yield return OmniRelayErrors.ToResult<Response<TResponse>>(encodeResult.Error!, options.Direction.ToString());
            yield break;
        }

        var rawRequest = new Request<ReadOnlyMemory<byte>>(meta, encodeResult.Value);
        var streamResult = await _pipeline(rawRequest, options, cancellationToken).ConfigureAwait(false);
        if (streamResult.IsFailure)
        {
            yield return OmniRelayErrors.ToResult<Response<TResponse>>(streamResult.Error!, options.Direction.ToString());
            yield break;
        }

        var callLease = streamResult.Value.AsAsyncDisposable(out var call);
        try
        {
            await foreach (var payload in call.Responses.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var decodeResult = _codec.DecodeResponse(payload, call.ResponseMeta);
                if (decodeResult.IsFailure)
                {
                    await call.CompleteAsync(decodeResult.Error!, cancellationToken).ConfigureAwait(false);
                    yield return OmniRelayErrors.ToResult<Response<TResponse>>(decodeResult.Error!, request.Meta.Transport ?? "stream");
                    yield break;
                }

                yield return Ok(Response<TResponse>.Create(decodeResult.Value, call.ResponseMeta));
            }
        }
        finally
        {
            await callLease.DisposeAsync().ConfigureAwait(false);
        }
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
