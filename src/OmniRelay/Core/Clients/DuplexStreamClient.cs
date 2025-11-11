using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Clients;

/// <summary>
/// Typed duplex-streaming RPC client that applies middleware and uses an <see cref="ICodec{TRequest,TResponse}"/>.
/// </summary>
public sealed class DuplexStreamClient<TRequest, TResponse>
{
    private readonly DuplexOutboundDelegate _pipeline;
    private readonly ICodec<TRequest, TResponse> _codec;

    /// <summary>
    /// Creates a duplex-streaming client bound to an outbound and codec.
    /// </summary>
    public DuplexStreamClient(
        IDuplexOutbound outbound,
        ICodec<TRequest, TResponse> codec,
        IReadOnlyList<IDuplexOutboundMiddleware> middleware)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        ArgumentNullException.ThrowIfNull(outbound);

        _pipeline = MiddlewareComposer.ComposeDuplexOutbound(middleware, outbound.CallAsync);
    }

    /// <summary>
    /// Starts a duplex-streaming session and returns a result-wrapped session wrapper for writing and reading.
    /// </summary>
    public async ValueTask<Result<DuplexStreamSession>> StartAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var normalized = EnsureEncoding(meta);

        var request = new Request<ReadOnlyMemory<byte>>(normalized, ReadOnlyMemory<byte>.Empty);
        var result = await _pipeline(request, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return OmniRelayErrors.ToResult<DuplexStreamSession>(result.Error!, normalized.Transport ?? "unknown");
        }

        return Ok(new DuplexStreamSession(normalized, _codec, result.Value));
    }

    /// <summary>
    /// Represents an active duplex-streaming session with request/response operations.
    /// </summary>
    public sealed class DuplexStreamSession : IAsyncDisposable
    {
        private readonly ICodec<TRequest, TResponse> _codec;
        private readonly IDuplexStreamCall _call;

        internal DuplexStreamSession(RequestMeta meta, ICodec<TRequest, TResponse> codec, IDuplexStreamCall call)
        {
            RequestMeta = meta;
            _codec = codec;
            _call = call;
        }

        /// <summary>Gets the request metadata.</summary>
        public RequestMeta RequestMeta { get; }

        /// <summary>Gets the response metadata.</summary>
        public ResponseMeta ResponseMeta => _call.ResponseMeta;

        /// <summary>Gets the raw request writer channel.</summary>
        public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _call.RequestWriter;

        /// <summary>Gets the raw response reader channel.</summary>
        public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _call.ResponseReader;

        /// <summary>Writes a typed request message to the request stream, returning a result for success or failure.</summary>
        public async ValueTask<Result<Unit>> WriteAsync(TRequest message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encodeResult = _codec.EncodeRequest(message, RequestMeta);
            if (encodeResult.IsFailure)
            {
                return OmniRelayErrors.ToResult<Unit>(encodeResult.Error!, RequestMeta.Transport ?? "unknown");
            }

            await _call.RequestWriter.WriteAsync(encodeResult.Value, cancellationToken).ConfigureAwait(false);
            return Ok(Unit.Value);
        }

        /// <summary>Signals completion of request writes, optionally with an error.</summary>
        public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _call.CompleteRequestsAsync(error, cancellationToken);

        /// <summary>Signals completion of response reads, optionally with an error.</summary>
        public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _call.CompleteResponsesAsync(error, cancellationToken);

        /// <summary>
        /// Reads and decodes response messages from the duplex session as an async stream of result-wrapped responses.
        /// </summary>
        public async IAsyncEnumerable<Result<Response<TResponse>>> ReadResponsesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var responses = _call.ResponseReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var enumerator = responses.GetAsyncEnumerator();
            Result<Response<TResponse>>? pendingFailure = null;
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        pendingFailure = OmniRelayErrors.ToResult<Response<TResponse>>(ex, RequestMeta.Transport ?? "unknown");
                        break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    var payload = enumerator.Current;
                    var decode = _codec.DecodeResponse(payload, _call.ResponseMeta);
                    if (decode.IsFailure)
                    {
                        await _call.CompleteResponsesAsync(decode.Error!, cancellationToken).ConfigureAwait(false);
                        yield return OmniRelayErrors.ToResult<Response<TResponse>>(decode.Error!, RequestMeta.Transport ?? "unknown");
                        yield break;
                    }

                    yield return Ok(Response<TResponse>.Create(decode.Value, _call.ResponseMeta));
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (pendingFailure is not null)
            {
                yield return pendingFailure.Value;
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _call.DisposeAsync();
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
