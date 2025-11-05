using System.Net.WebSockets;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Transport.Http;

internal sealed class HttpDuplexStreamTransportCall : IDuplexStreamCall
{
    private const int BufferSize = 32 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly DuplexStreamCall _inner;
    private readonly CancellationTokenSource _cts;
    private readonly string _transport;
    private Task? _requestPump;
    private Task? _responsePump;

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
        _requestPump = PumpRequestsAsync(_cts.Token);
        _responsePump = PumpResponsesAsync(_cts.Token);
    }

    public RequestMeta RequestMeta => _inner.RequestMeta;

    public ResponseMeta ResponseMeta => _inner.ResponseMeta;

    public DuplexStreamCallContext Context => _inner.Context;

    public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _inner.RequestWriter;

    public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _inner.RequestReader;

    public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _inner.ResponseWriter;

    public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _inner.ResponseReader;

    public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteRequestsAsync(error, cancellationToken);

    public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteResponsesAsync(error, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        try
        {
            if (_requestPump is not null || _responsePump is not null)
            {
                await Task.WhenAll(
                        _requestPump ?? Task.CompletedTask,
                        _responsePump ?? Task.CompletedTask)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // swallow pump exceptions during disposal
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

    private async Task PumpRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _inner.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await HttpDuplexProtocol.SendFrameAsync(_socket, HttpDuplexProtocol.FrameType.RequestData, payload, cancellationToken).ConfigureAwait(false);
            }

            await HttpDuplexProtocol.SendFrameAsync(_socket, HttpDuplexProtocol.FrameType.RequestComplete, CancellationToken.None).ConfigureAwait(false);
            await _inner.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                "The request stream was cancelled.",
                transport: _transport);
            await HttpDuplexProtocol.SendFrameAsync(_socket, HttpDuplexProtocol.FrameType.RequestError, HttpDuplexProtocol.CreateErrorPayload(error), CancellationToken.None).ConfigureAwait(false);
            await _inner.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var omni = OmniRelayErrors.FromException(ex, _transport);
            var error = omni.Error;
            await HttpDuplexProtocol.SendFrameAsync(_socket, HttpDuplexProtocol.FrameType.RequestError, HttpDuplexProtocol.CreateErrorPayload(error), CancellationToken.None).ConfigureAwait(false);
            await _inner.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await HttpDuplexProtocol.ReceiveFrameAsync(_socket, buffer, cancellationToken).ConfigureAwait(false);

                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    await _inner.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                switch (frame.Type)
                {
                    case HttpDuplexProtocol.FrameType.ResponseHeaders:
                        {
                            var meta = HttpDuplexProtocol.DeserializeResponseMeta(frame.Payload.Span, _transport);
                            _inner.SetResponseMeta(meta);
                            break;
                        }

                    case HttpDuplexProtocol.FrameType.ResponseData:
                        await _inner.ResponseWriter.WriteAsync(frame.Payload.ToArray(), cancellationToken).ConfigureAwait(false);
                        break;

                    case HttpDuplexProtocol.FrameType.ResponseComplete:
                        {
                            if (!frame.Payload.IsEmpty)
                            {
                                var meta = HttpDuplexProtocol.DeserializeResponseMeta(frame.Payload.Span, _transport);
                                _inner.SetResponseMeta(meta);
                            }

                            await _inner.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                    case HttpDuplexProtocol.FrameType.ResponseError:
                        {
                            var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, _transport);
                            await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                    case HttpDuplexProtocol.FrameType.RequestComplete:
                        await _inner.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        break;

                    case HttpDuplexProtocol.FrameType.RequestError:
                        {
                            var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, _transport);
                            await _inner.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                            await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                    case HttpDuplexProtocol.FrameType.RequestData:
                        await _inner.RequestWriter.WriteAsync(frame.Payload.ToArray(), cancellationToken).ConfigureAwait(false);
                        break;
                }
            }

            await _inner.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _inner.CompleteResponsesAsync(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                "The response stream was cancelled.",
                transport: _transport), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var omni = OmniRelayErrors.FromException(ex, _transport);
            await _inner.CompleteResponsesAsync(omni.Error, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
