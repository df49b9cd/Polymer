using System.Net.WebSockets;
using System.Text.Json;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Http;

internal static class HttpDuplexProtocol
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

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static async ValueTask SendFrameAsync(
        WebSocket socket,
        FrameType frameType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[payload.Length + 1];
        buffer[0] = (byte)frameType;
        if (!payload.IsEmpty)
        {
            payload.Span.CopyTo(buffer.AsSpan(1));
        }

        await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    internal static ValueTask SendFrameAsync(WebSocket socket, FrameType frameType, CancellationToken cancellationToken) =>
        SendFrameAsync(socket, frameType, ReadOnlyMemory<byte>.Empty, cancellationToken);

    internal static async ValueTask<Frame> ReceiveFrameAsync(
        WebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var position = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, position, buffer.Length - position), cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new Frame(WebSocketMessageType.Close, default, ReadOnlyMemory<byte>.Empty);
            }

            position += result.Count;
        }
        while (!result.EndOfMessage && position < buffer.Length);

        if (position == 0)
        {
            return new Frame(result.MessageType, default, ReadOnlyMemory<byte>.Empty);
        }

        var span = buffer.AsSpan(0, position);
        var type = (FrameType)span[0];
        var payload = span.Length > 1 ? new ReadOnlyMemory<byte>(buffer, 1, span.Length - 1) : ReadOnlyMemory<byte>.Empty;
        return new Frame(result.MessageType, type, payload);
    }

    internal static ReadOnlyMemory<byte> CreateErrorPayload(Error error)
    {
        var envelope = new ErrorEnvelope
        {
            Status = OmniRelayErrorAdapter.ToStatus(error).ToString(),
            Message = error.Message,
            Code = error.Code
        };

        return JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
    }

    internal static ReadOnlyMemory<byte> SerializeResponseMeta(ResponseMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var envelope = new ResponseMetaEnvelope
        {
            Encoding = meta.Encoding,
            Transport = meta.Transport,
            TtlMs = meta.Ttl?.TotalMilliseconds,
            Headers = meta.Headers?.Count > 0
                ? meta.Headers!.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                : null
        };

        return JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
    }

    internal static ResponseMeta DeserializeResponseMeta(ReadOnlySpan<byte> payload, string transport)
    {
        if (payload.IsEmpty)
        {
            return new ResponseMeta(transport: transport);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ResponseMetaEnvelope>(payload, SerializerOptions);
            if (envelope is null)
            {
                return new ResponseMeta(transport: transport);
            }

            TimeSpan? ttl = null;
            if (envelope.TtlMs.HasValue)
            {
                ttl = TimeSpan.FromMilliseconds(envelope.TtlMs.Value);
            }

            IEnumerable<KeyValuePair<string, string>>? headers = null;
            if (envelope.Headers is { Count: > 0 })
            {
                headers = envelope.Headers.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value));
            }

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

    internal static Error ParseError(ReadOnlySpan<byte> payload, string transport)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ErrorEnvelope>(payload, SerializerOptions);
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

    internal readonly record struct Frame(WebSocketMessageType MessageType, FrameType Type, ReadOnlyMemory<byte> Payload);

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
}
