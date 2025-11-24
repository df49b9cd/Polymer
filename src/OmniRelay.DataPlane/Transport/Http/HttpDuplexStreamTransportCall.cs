using System.Net.WebSockets;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Transport.Http;

/// <summary>
/// Transport-specific wrapper for duplex streaming calls over HTTP WebSockets.
/// Bridges WebSocket frames to the OmniRelay duplex streaming abstractions.
/// </summary>
internal sealed class HttpDuplexStreamTransportCall : IDuplexStreamCall, IResultDuplexStreamCall
{
    private const int BufferSize = 32 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly DuplexStreamCall _inner;
    private readonly CancellationTokenSource _cts;
    private readonly string _transport;
    private ErrGroup? _pumpGroup;

    private HttpDuplexStreamTransportCall(
        ClientWebSocket socket,
        DuplexStreamCall inner,
        string transport,
        CancellationToken cancellationToken)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _transport = transport;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>
    /// Creates a transport-backed duplex stream call using a connected WebSocket.
    /// </summary>
    /// <param name="requestMeta">The request metadata.</param>
    /// <param name="responseMeta">Initial response metadata.</param>
    /// <param name="socket">The connected WebSocket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created duplex stream call or an error.</returns>
    public static async ValueTask<Result<IDuplexStreamCall>> CreateAsync(
        RequestMeta requestMeta,
        ResponseMeta responseMeta,
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestMeta);
        ArgumentNullException.ThrowIfNull(responseMeta);
        ArgumentNullException.ThrowIfNull(socket);

        var transport = requestMeta.Transport ?? "http";

        try
        {
            var inner = DuplexStreamCall.Create(requestMeta, responseMeta);
            var call = new HttpDuplexStreamTransportCall(socket, inner, transport, cancellationToken);
            call.StartPumps();
            return Ok<IDuplexStreamCall>(call);
        }
        catch (Exception ex)
        {
            return OmniRelayErrors.ToResult<IDuplexStreamCall>(ex, transport: transport);
        }
    }

    private void StartPumps()
    {
        var group = new ErrGroup(_cts.Token);

        group.Go((_, token) => PumpRequestsAsync(token));
        group.Go((_, token) => PumpResponsesAsync(token));

        _pumpGroup = group;
    }

    /// <inheritdoc />
    public RequestMeta RequestMeta => _inner.RequestMeta;

    /// <inheritdoc />
    public ResponseMeta ResponseMeta => _inner.ResponseMeta;

    /// <inheritdoc />
    public DuplexStreamCallContext Context => _inner.Context;

    /// <inheritdoc />
    public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _inner.RequestWriter;

    /// <inheritdoc />
    public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _inner.RequestReader;

    /// <inheritdoc />
    public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _inner.ResponseWriter;

    /// <inheritdoc />
    public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _inner.ResponseReader;

    /// <inheritdoc />
    public ValueTask CompleteRequestsAsync(Error? fault = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteRequestsAsync(fault, cancellationToken);

    /// <inheritdoc />
    public ValueTask CompleteResponsesAsync(Error? fault = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteResponsesAsync(fault, cancellationToken);

    async ValueTask<Result<Unit>> IResultDuplexStreamCall.CompleteRequestsResultAsync(Error? fault, CancellationToken cancellationToken)
    {
        try
        {
            await _inner.CompleteRequestsAsync(fault, cancellationToken).ConfigureAwait(false);
            return Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            return OmniRelayErrors.ToResult<Unit>(ex, _transport);
        }
    }

    async ValueTask<Result<Unit>> IResultDuplexStreamCall.CompleteResponsesResultAsync(Error? fault, CancellationToken cancellationToken)
    {
        try
        {
            await _inner.CompleteResponsesAsync(fault, cancellationToken).ConfigureAwait(false);
            return Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            return OmniRelayErrors.ToResult<Unit>(ex, _transport);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_pumpGroup is not null)
        {
            var hasPumpResult = false;
            Result<Unit> pumpResult = default;

            try
            {
                pumpResult = await _pumpGroup.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                hasPumpResult = true;
            }
            catch (Exception ex)
            {
                await HandlePumpGroupFailureAsync(Error.FromException(ex)).ConfigureAwait(false);
            }
            finally
            {
                _pumpGroup.Dispose();
                _pumpGroup = null;
            }

            if (hasPumpResult && pumpResult.IsFailure && pumpResult.Error is { } pumpError)
            {
                await HandlePumpGroupFailureAsync(pumpError).ConfigureAwait(false);
            }
        }

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "completed", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore close failures
            }
        }

        _socket.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private ValueTask<Result<Unit>> PumpRequestsAsync(CancellationToken cancellationToken)
    {
        async ValueTask<Result<Unit>> SendAsync(CancellationToken token)
        {
            var stream = Result.MapStreamAsync(
                _inner.RequestReader.ReadAllAsync(token),
                (payload, ct) => HttpDuplexProtocol.SendFrameResultAsync(
                    _socket,
                    HttpDuplexProtocol.FrameType.RequestData,
                    payload,
                    _transport,
                    ct),
                token);

            await foreach (var result in stream.ConfigureAwait(false))
            {
                if (result.IsFailure)
                {
                    var normalized = NormalizeResultError(result.Error);
                    var errorPayload = HttpDuplexProtocol.CreateErrorPayload(normalized);
                    await HttpDuplexProtocol.SendFrameResultAsync(
                            _socket,
                            HttpDuplexProtocol.FrameType.RequestError,
                            errorPayload,
                            _transport,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await _inner.CompleteRequestsAsync(normalized, CancellationToken.None).ConfigureAwait(false);
                    return Err<Unit>(normalized);
                }
            }

            var completionResult = await HttpDuplexProtocol.SendFrameResultAsync(
                    _socket,
                    HttpDuplexProtocol.FrameType.RequestComplete,
                    _transport,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (completionResult.IsFailure)
            {
                var completionError = NormalizeResultError(completionResult.Error);
                await _inner.CompleteRequestsAsync(completionError, CancellationToken.None).ConfigureAwait(false);
                return Err<Unit>(completionError);
            }

            await _inner.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return Ok(Unit.Value);
        }

        return SendAsync(cancellationToken);
    }

    private ValueTask<Result<Unit>> PumpResponsesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];

        async ValueTask<Result<Unit>> ReceiveAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var frameResult = await HttpDuplexProtocol.ReceiveFrameAsync(
                        _socket,
                        buffer,
                        BufferSize - 1,
                        _transport,
                        token)
                    .ConfigureAwait(false);

                if (frameResult.IsFailure)
                {
                    var error = NormalizeResultError(frameResult.Error);
                    await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                    return Err<Unit>(error);
                }

                var frame = frameResult.Value;

                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    await _inner.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    return Ok(Unit.Value);
                }

                switch (frame.Type)
                {
                    case HttpDuplexProtocol.FrameType.ResponseHeaders:
                        _inner.SetResponseMeta(HttpDuplexProtocol.DeserializeResponseMeta(frame.Payload.Span, _transport));
                        break;

                    case HttpDuplexProtocol.FrameType.ResponseData:
                        await _inner.ResponseWriter.WriteAsync(CopyFramePayload(frame.Payload), token).ConfigureAwait(false);
                        break;

                    case HttpDuplexProtocol.FrameType.ResponseComplete:
                        if (!frame.Payload.IsEmpty)
                        {
                            _inner.SetResponseMeta(HttpDuplexProtocol.DeserializeResponseMeta(frame.Payload.Span, _transport));
                        }

                        await _inner.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        return Ok(Unit.Value);

                    case HttpDuplexProtocol.FrameType.ResponseError:
                        {
                            var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, _transport);
                            await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                            return Err<Unit>(error);
                        }

                    case HttpDuplexProtocol.FrameType.RequestComplete:
                        await _inner.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        break;

                    case HttpDuplexProtocol.FrameType.RequestError:
                        {
                            var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, _transport);
                            await _inner.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                            await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                            return Err<Unit>(error);
                        }

                    case HttpDuplexProtocol.FrameType.RequestData:
                        await _inner.RequestWriter.WriteAsync(frame.Payload, token).ConfigureAwait(false);
                        break;
                }
            }

            return Ok(Unit.Value);
        }

        return ReceiveAsync(cancellationToken);
    }

    private static byte[] CopyFramePayload(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        var copy = GC.AllocateUninitializedArray<byte>(payload.Length);
        payload.Span.CopyTo(copy);
        return copy;
    }

    private async ValueTask HandlePumpGroupFailureAsync(Error pumpError)
    {
        await _inner.CompleteRequestsAsync(pumpError, CancellationToken.None).ConfigureAwait(false);
        await _inner.CompleteResponsesAsync(pumpError, CancellationToken.None).ConfigureAwait(false);
    }

    private Error NormalizeResultError(Error? error) =>
        OmniRelayErrors.FromError(error ?? Error.Unspecified(), _transport).Error;
}
