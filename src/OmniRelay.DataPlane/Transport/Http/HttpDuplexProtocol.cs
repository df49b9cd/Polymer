using System.Buffers;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hugo;
using static Hugo.Go;
using OmniRelay.Core;
using OmniRelay.Errors;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.Transport.Http;

/// <summary>
/// Internal framing protocol for OmniRelay HTTP duplex streaming over WebSockets.
/// Handles framing, serialization, and error envelopes.
/// </summary>
internal static partial class HttpDuplexProtocol
{
    internal enum FrameType : byte
    {
        RequestData = 0x1,
        RequestComplete = 0x2,
        RequestError = 0x3,
        ResponseHeaders = 0x10,
        ResponseData = 0x11,
        ResponseComplete = 0x12,
        ResponseError = 0x13
    }

    private static readonly HttpDuplexProtocolJsonContext JsonContext = HttpDuplexProtocolJsonContext.Default;
    internal const int MaxPooledSendBytes = 128 * 1024;
    internal static ArrayPool<byte> FrameBufferPool { get; set; } = ArrayPool<byte>.Shared;

    /// <summary>
    /// Sends a framed WebSocket message with the specified frame type and payload.
    /// </summary>
    /// <param name="socket">The WebSocket connection.</param>
    /// <param name="frameType">The frame type discriminator.</param>
    /// <param name="payload">Optional payload to append after the frame byte.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async ValueTask SendFrameAsync(
        WebSocket socket,
        FrameType frameType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var length = payload.Length + 1;
        ArrayPool<byte>? pool = null;
        var buffer = length <= MaxPooledSendBytes && (pool = FrameBufferPool) is not null
            ? pool.Rent(length)
            : GC.AllocateUninitializedArray<byte>(length);

        try
        {
            buffer[0] = (byte)frameType;
            if (!payload.IsEmpty)
            {
                payload.Span.CopyTo(buffer.AsSpan(1));
            }

            await socket.SendAsync(new ArraySegment<byte>(buffer, 0, length), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pool?.Return(buffer, clearArray: false);
        }
    }

    /// <summary>
    /// Sends a framed WebSocket message with no payload.
    /// </summary>
    /// <param name="socket">The WebSocket connection.</param>
    /// <param name="frameType">The frame type discriminator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static ValueTask SendFrameAsync(WebSocket socket, FrameType frameType, CancellationToken cancellationToken) =>
        SendFrameAsync(socket, frameType, ReadOnlyMemory<byte>.Empty, cancellationToken);

    /// <summary>
    /// Receives a single framed WebSocket message ensuring the payload does not exceed the configured limit.
    /// </summary>
    /// <param name="socket">The WebSocket connection.</param>
    /// <param name="buffer">The buffer to receive into.</param>
    /// <param name="maxPayloadBytes">Maximum allowed payload size.</param>
    /// <param name="transport">Transport name used for error attribution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The received frame.</returns>
    internal static async ValueTask<Result<Frame>> ReceiveFrameAsync(
        WebSocket socket,
        byte[] buffer,
        int maxPayloadBytes,
        string transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(buffer);

        var position = 0;

        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, position, buffer.Length - position), cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return Ok(new Frame(WebSocketMessageType.Close, default, ReadOnlyMemory<byte>.Empty));
                }

                position += result.Count;

                if (position > buffer.Length)
                {
                    return Err<Frame>(CreateFrameSizeError(maxPayloadBytes, transport));
                }

                if (position > 0 && (position - 1) > maxPayloadBytes)
                {
                    return Err<Frame>(CreateFrameSizeError(maxPayloadBytes, transport));
                }
            }
            while (!result.EndOfMessage && position < buffer.Length);

            if (!result.EndOfMessage && position >= buffer.Length)
            {
                return Err<Frame>(CreateFrameSizeError(maxPayloadBytes, transport));
            }

            if (position == 0)
            {
                return Ok(new Frame(result.MessageType, default, ReadOnlyMemory<byte>.Empty));
            }

            var span = buffer.AsSpan(0, position);
            var type = (FrameType)span[0];
            var payload = span.Length > 1 ? new ReadOnlyMemory<byte>(buffer, 1, span.Length - 1) : ReadOnlyMemory<byte>.Empty;
            return Ok(new Frame(result.MessageType, type, payload));
        }
        catch (OperationCanceledException canceled)
        {
            return Err<Frame>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                string.IsNullOrWhiteSpace(canceled.Message) ? "The duplex frame read was cancelled." : canceled.Message,
                transport,
                inner: Error.Canceled().WithCause(canceled)));
        }
        catch (WebSocketException socketException) when (socketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            return Err<Frame>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                "The WebSocket connection closed prematurely.",
                transport,
                inner: Error.FromException(socketException)));
        }
        catch (Exception ex)
        {
            return OmniRelayErrors.ToResult<Frame>(ex, transport);
        }
    }

    [Obsolete("Use overload with transport and Result return to avoid throwing in business logic.")]
    internal static async ValueTask<Frame> ReceiveFrameAsync(
        WebSocket socket,
        byte[] buffer,
        int maxPayloadBytes,
        CancellationToken cancellationToken) =>
        (await ReceiveFrameAsync(socket, buffer, maxPayloadBytes, transport: "http", cancellationToken).ConfigureAwait(false))
            .ValueOrThrow();

    internal static async ValueTask<Result<Unit>> SendFrameResultAsync(
        WebSocket socket,
        FrameType frameType,
        ReadOnlyMemory<byte> payload,
        string transport,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendFrameAsync(socket, frameType, payload, cancellationToken).ConfigureAwait(false);
            return Ok(Unit.Value);
        }
        catch (OperationCanceledException canceled)
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                string.IsNullOrWhiteSpace(canceled.Message) ? "The duplex frame send was cancelled." : canceled.Message,
                transport,
                inner: Error.Canceled().WithCause(canceled)));
        }
        catch (WebSocketException socketException) when (socketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                "The WebSocket connection closed prematurely.",
                transport,
                inner: Error.FromException(socketException)));
        }
        catch (Exception ex)
        {
            return OmniRelayErrors.ToResult<Unit>(ex, transport);
        }
    }

    internal static ValueTask<Result<Unit>> SendFrameResultAsync(
        WebSocket socket,
        FrameType frameType,
        string transport,
        CancellationToken cancellationToken) =>
        SendFrameResultAsync(socket, frameType, ReadOnlyMemory<byte>.Empty, transport, cancellationToken);

    /// <summary>
    /// Serializes an OmniRelay error to a JSON envelope payload.
    /// </summary>
    /// <param name="error">The error to serialize.</param>
    /// <returns>UTF-8 JSON payload.</returns>
    internal static ReadOnlyMemory<byte> CreateErrorPayload(Error error)
    {
        var envelope = new ErrorEnvelope
        {
            Status = OmniRelayErrorAdapter.ToStatus(error).ToString(),
            Message = error.Message,
            Code = error.Code
        };

        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonContext.ErrorEnvelope);
    }

    /// <summary>
    /// Serializes response metadata to a JSON envelope payload.
    /// </summary>
    /// <param name="meta">The response metadata.</param>
    /// <returns>UTF-8 JSON payload.</returns>
    internal static ReadOnlyMemory<byte> SerializeResponseMeta(ResponseMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var envelope = new ResponseMetaEnvelope
        {
            Encoding = meta.Encoding,
            Transport = meta.Transport,
            TtlMs = meta.Ttl?.TotalMilliseconds,
            Headers = BuildHeaders(meta.Headers)
        };

        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonContext.ResponseMetaEnvelope);
    }

    /// <summary>
    /// Deserializes response metadata from a JSON envelope payload.
    /// </summary>
    /// <param name="payload">The serialized payload.</param>
    /// <param name="transport">Transport name fallback.</param>
    /// <returns>The response metadata.</returns>
    internal static ResponseMeta DeserializeResponseMeta(ReadOnlySpan<byte> payload, string transport)
    {
        if (payload.IsEmpty)
        {
            return new ResponseMeta(transport: transport);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize(payload, JsonContext.ResponseMetaEnvelope);
            if (envelope is null)
            {
                return new ResponseMeta(transport: transport);
            }

            TimeSpan? ttl = null;
            if (envelope.TtlMs.HasValue)
            {
                ttl = TimeSpan.FromMilliseconds(envelope.TtlMs.Value);
            }

            IEnumerable<KeyValuePair<string, string>>? headers = envelope.Headers is { Count: > 0 }
                ? envelope.Headers
                : null;

            return new ResponseMeta(
                encoding: envelope.Encoding,
                transport: envelope.Transport ?? transport,
                ttl: ttl,
                headers: headers);
        }
        catch (JsonException)
        {
            return new ResponseMeta(transport: transport);
        }
    }

    /// <summary>
    /// Parses an OmniRelay error from a JSON envelope payload.
    /// </summary>
    /// <param name="payload">The serialized payload.</param>
    /// <param name="transport">Transport name for attribution.</param>
    /// <returns>The error value.</returns>
    internal static Error ParseError(ReadOnlySpan<byte> payload, string transport)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(payload, JsonContext.ErrorEnvelope);
            if (envelope is null)
            {
                return OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "duplex error", transport: transport);
            }

            var status = OmniRelayStatusCode.Internal;
            if (!string.IsNullOrWhiteSpace(envelope.Status) &&
                Enum.TryParse(envelope.Status, true, out OmniRelayStatusCode parsed))
            {
                status = parsed;
            }

            var message = string.IsNullOrWhiteSpace(envelope.Message) ? status.ToString() : envelope.Message!;
            var error = OmniRelayErrorAdapter.FromStatus(status, message, transport: transport);

            if (!string.IsNullOrWhiteSpace(envelope.Code))
            {
                error = error.WithCode(envelope.Code);
            }

            return error;
        }
        catch (JsonException)
        {
            return OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "duplex error", transport: transport);
        }
    }

    internal readonly record struct Frame(WebSocketMessageType MessageType, FrameType Type, ReadOnlyMemory<byte> Payload)
    {
        public WebSocketMessageType MessageType { get; init; } = MessageType;

        public FrameType Type { get; init; } = Type;

        public ReadOnlyMemory<byte> Payload { get; init; } = Payload;
    }

    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Web,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ErrorEnvelope))]
    [JsonSerializable(typeof(ResponseMetaEnvelope))]
    private sealed partial class HttpDuplexProtocolJsonContext : JsonSerializerContext;

    private sealed class ErrorEnvelope
    {
        public string? Status { get; set; }

        public string? Message { get; set; }

        public string? Code { get; set; }
    }

    private sealed class ResponseMetaEnvelope
    {
        public string? Encoding { get; set; }

        public string? Transport { get; set; }

        public double? TtlMs { get; set; }

        public Dictionary<string, string>? Headers { get; set; }
    }

    private static Dictionary<string, string>? BuildHeaders(ImmutableDictionary<string, string> headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var copy = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers)
        {
            copy[pair.Key] = pair.Value;
        }

        return copy;
    }

    private static Error CreateFrameSizeError(int maxPayloadBytes, string transport) =>
        OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.ResourceExhausted,
            $"Duplex frame exceeds the configured maximum size ({maxPayloadBytes} bytes).",
            transport);
}
