using System.Buffers;
using System.Net.WebSockets;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class HttpDuplexProtocolTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask SendFrameAsync_UsesPooledBuffer_AndFramesPayload()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        var trackingPool = new TrackingArrayPool<byte>();
        var socket = new RecordingWebSocket();
        var previousPool = HttpDuplexProtocol.FrameBufferPool;

        try
        {
            HttpDuplexProtocol.FrameBufferPool = trackingPool;

            await HttpDuplexProtocol.SendFrameAsync(
                socket,
                HttpDuplexProtocol.FrameType.ResponseData,
                payload,
                CancellationToken.None);

            trackingPool.RentCount.ShouldBe(1);
            trackingPool.ReturnCount.ShouldBe(1);

            socket.SentMessages.ShouldHaveSingleItem();
            var sent = socket.SentMessages[0];
            sent.MessageType.ShouldBe(WebSocketMessageType.Binary);
            sent.EndOfMessage.ShouldBeTrue();
            sent.Buffer.ShouldBe(new byte[] { (byte)HttpDuplexProtocol.FrameType.ResponseData, 0xAA, 0xBB, 0xCC });
        }
        finally
        {
            HttpDuplexProtocol.FrameBufferPool = previousPool;
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask SendFrameAsync_LargePayload_SkipsPool()
    {
        var payload = GC.AllocateUninitializedArray<byte>(HttpDuplexProtocol.MaxPooledSendBytes + 8);
        for (var i = 0; i < 8; i++)
        {
            payload[i] = (byte)(i + 1);
        }

        var trackingPool = new TrackingArrayPool<byte>();
        var socket = new RecordingWebSocket();
        var previousPool = HttpDuplexProtocol.FrameBufferPool;

        try
        {
            HttpDuplexProtocol.FrameBufferPool = trackingPool;

            await HttpDuplexProtocol.SendFrameAsync(
                socket,
                HttpDuplexProtocol.FrameType.RequestData,
                payload,
                CancellationToken.None);

            trackingPool.RentCount.ShouldBe(0);
            trackingPool.ReturnCount.ShouldBe(0);

            socket.SentMessages.ShouldHaveSingleItem();
            var sent = socket.SentMessages[0];
            sent.Buffer[0].ShouldBe((byte)HttpDuplexProtocol.FrameType.RequestData);
            sent.Buffer[1].ShouldBe((byte)1);
            sent.Buffer[8].ShouldBe((byte)8);
            sent.Buffer.Length.ShouldBe(payload.Length + 1);
        }
        finally
        {
            HttpDuplexProtocol.FrameBufferPool = previousPool;
        }
    }

    private sealed record SentMessage(byte[] Buffer, WebSocketMessageType MessageType, bool EndOfMessage);

    private sealed class RecordingWebSocket : WebSocket
    {
        public List<SentMessage> SentMessages { get; } = new();

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => string.Empty;

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentMessages.Add(new SentMessage(buffer.ToArray(), messageType, endOfMessage));
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentMessages.Add(new SentMessage(buffer.ToArray(), messageType, endOfMessage));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingArrayPool<T> : ArrayPool<T>
    {
        private int rentCount;
        private int returnCount;

        public override T[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref rentCount);
            return new T[minimumLength];
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            if (clearArray)
            {
                Array.Clear(array);
            }

            Interlocked.Increment(ref returnCount);
        }

        public int RentCount => rentCount;

        public int ReturnCount => returnCount;
    }
}
