using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;

namespace Polymer.Transport.Http;

internal sealed class HttpDuplexStreamTransportCall : IDuplexStreamCall
{
    private readonly ClientWebSocket _socket;
    private readonly DuplexStreamCall _inner;
    private readonly CancellationTokenSource _cts;
    private readonly Task _requestPump;
    private readonly Task _responsePump;

    private HttpDuplexStreamTransportCall(ClientWebSocket socket, DuplexStreamCall inner, CancellationToken cancellationToken)
    {
        _socket = socket;
        _inner = inner;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _requestPump = PumpRequestsAsync(_cts.Token);
        _responsePump = PumpResponsesAsync(_cts.Token);
    }

    public static IDuplexStreamCall Create(
        RequestMeta requestMeta,
        ResponseMeta responseMeta,
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var inner = DuplexStreamCall.Create(requestMeta, responseMeta);
        return new HttpDuplexStreamTransportCall(socket, inner, cancellationToken);
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
        _cts.Cancel();

        try
        {
            await Task.WhenAll(_requestPump, _responsePump).ConfigureAwait(false);
        }
        catch
        {
            // ignored
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
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Cancelled,
                "The request stream was cancelled.",
                transport: "http");
            await HttpDuplexProtocol.SendFrameAsync(_socket, HttpDuplexProtocol.FrameType.RequestError, HttpDuplexProtocol.CreateErrorPayload(error), CancellationToken.None).ConfigureAwait(false);
            await _inner.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while sending request messages.",
                transport: "http",
                inner: Error.FromException(ex));
            await HttpDuplexProtocol.SendFrameAsync(_socket, HttpDuplexProtocol.FrameType.RequestError, HttpDuplexProtocol.CreateErrorPayload(error), CancellationToken.None).ConfigureAwait(false);
            await _inner.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];

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
                    case HttpDuplexProtocol.FrameType.ResponseData:
                        await _inner.ResponseWriter.WriteAsync(frame.Payload.ToArray(), cancellationToken).ConfigureAwait(false);
                        break;

                    case HttpDuplexProtocol.FrameType.ResponseComplete:
                        await _inner.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        return;

                    case HttpDuplexProtocol.FrameType.ResponseError:
                    {
                        var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, "http");
                        await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    case HttpDuplexProtocol.FrameType.RequestComplete:
                        await _inner.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        break;

                    case HttpDuplexProtocol.FrameType.RequestError:
                    {
                        var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, "http");
                        await _inner.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                        await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                }
            }

            await _inner.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _inner.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Cancelled,
                "The response stream was cancelled.",
                transport: "http"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException wsEx) when (wsEx.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            await _inner.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _inner.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while receiving response messages.",
                transport: "http",
                inner: Error.FromException(ex)), CancellationToken.None).ConfigureAwait(false);
        }
    }
}
