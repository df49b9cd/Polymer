using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core;

/// <summary>
/// Protobuf codec supporting binary and optional JSON encodings with robust error mapping.
/// </summary>
public sealed class ProtobufCodec<TRequest, TResponse>(
    MessageParser<TRequest>? requestParser = null,
    MessageParser<TResponse>? responseParser = null,
    JsonParser? jsonParser = null,
    JsonFormatter? jsonFormatter = null,
    string? defaultEncoding = null,
    bool allowJsonEncoding = true)
    : ICodec<TRequest, TResponse>
    where TRequest : class, IMessage<TRequest>
    where TResponse : class, IMessage<TResponse>
{
    private const string EncodeRequestStage = "encode-request";
    private const string DecodeRequestStage = "decode-request";
    private const string EncodeResponseStage = "encode-response";
    private const string DecodeResponseStage = "decode-response";

    private readonly MessageParser<TRequest> _requestParser = ResolveParser(requestParser);
    private readonly MessageParser<TResponse> _responseParser = ResolveParser(responseParser);
    private readonly MessageDescriptor _requestDescriptor = ResolveDescriptor<TRequest>();
    private readonly MessageDescriptor _responseDescriptor = ResolveDescriptor<TResponse>();
    private readonly JsonParser _jsonParser = jsonParser ?? JsonParser.Default;
    private readonly JsonFormatter _jsonFormatter = jsonFormatter ?? JsonFormatter.Default;

    /// <inheritdoc />
    public string Encoding { get; } = string.IsNullOrWhiteSpace(defaultEncoding)
        ? ProtobufEncoding.Protobuf
        : defaultEncoding;

    /// <inheritdoc />
    public Result<byte[]> EncodeRequest(TRequest value, RequestMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        try
        {
            return EncodeMessage(value, meta.Encoding, EncodeRequestStage);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidOperationException)
        {
            return Err<byte[]>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                $"Failed to encode Protobuf request for procedure '{meta.Procedure ?? "unknown"}'.",
                ex,
                EncodeRequestStage,
                meta.Encoding));
        }
        catch (Exception ex)
        {
            return Err<byte[]>(CreateError(
                OmniRelayStatusCode.Internal,
                $"Unexpected error while encoding Protobuf request for procedure '{meta.Procedure ?? "unknown"}'.",
                ex,
                EncodeRequestStage,
                meta.Encoding));
        }
    }

    /// <inheritdoc />
    public Result<TRequest> DecodeRequest(ReadOnlyMemory<byte> payload, RequestMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var resolvedEncoding = ResolveEncoding(meta.Encoding, DecodeRequestStage);
        if (resolvedEncoding.IsFailure)
        {
            return Err<TRequest>(resolvedEncoding.Error!);
        }

        try
        {
            var message = DecodeMessage(payload, resolvedEncoding.Value, _requestParser, _requestDescriptor);
            return Ok(message);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return Err<TRequest>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                $"Failed to decode Protobuf request for procedure '{meta.Procedure ?? "unknown"}'.",
                ex,
                DecodeRequestStage,
                meta.Encoding));
        }
        catch (Exception ex)
        {
            return Err<TRequest>(CreateError(
                OmniRelayStatusCode.Internal,
                $"Unexpected error while decoding Protobuf request for procedure '{meta.Procedure ?? "unknown"}'.",
                ex,
                DecodeRequestStage,
                meta.Encoding));
        }
    }

    /// <inheritdoc />
    public Result<byte[]> EncodeResponse(TResponse value, ResponseMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        try
        {
            return EncodeMessage(value, meta.Encoding, EncodeResponseStage);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidOperationException)
        {
            return Err<byte[]>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                "Failed to encode Protobuf response payload.",
                ex,
                EncodeResponseStage,
                meta.Encoding));
        }
        catch (Exception ex)
        {
            return Err<byte[]>(CreateError(
                OmniRelayStatusCode.Internal,
                "Unexpected error while encoding Protobuf response payload.",
                ex,
                EncodeResponseStage,
                meta.Encoding));
        }
    }

    /// <inheritdoc />
    public Result<TResponse> DecodeResponse(ReadOnlyMemory<byte> payload, ResponseMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var resolvedEncoding = ResolveEncoding(meta.Encoding, DecodeResponseStage);
        if (resolvedEncoding.IsFailure)
        {
            return Err<TResponse>(resolvedEncoding.Error!);
        }

        try
        {
            var message = DecodeMessage(payload, resolvedEncoding.Value, _responseParser, _responseDescriptor);
            return Ok(message);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return Err<TResponse>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                "Failed to decode Protobuf response payload.",
                ex,
                DecodeResponseStage,
                meta.Encoding));
        }
        catch (Exception ex)
        {
            return Err<TResponse>(CreateError(
                OmniRelayStatusCode.Internal,
                "Unexpected error while decoding Protobuf response payload.",
                ex,
                DecodeResponseStage,
                meta.Encoding));
        }
    }

    private Result<byte[]> EncodeMessage<TMessage>(TMessage value, string? encoding, string stage)
        where TMessage : class, IMessage<TMessage>
    {
        if (value is null)
        {
            return Err<byte[]>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                "Protobuf value cannot be null.",
                new InvalidOperationException("Value cannot be null."),
                stage,
                encoding));
        }

        var resolvedEncoding = ResolveEncoding(encoding, stage);
        if (resolvedEncoding.IsFailure)
        {
            return Err<byte[]>(resolvedEncoding.Error!);
        }

        return resolvedEncoding.Value.Kind switch
        {
            EncodingKind.Binary => Ok(value.ToByteArray()),
            EncodingKind.Json => Ok(System.Text.Encoding.UTF8.GetBytes(_jsonFormatter.Format(value))),
            _ => Err<byte[]>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                $"Unsupported Protobuf encoding '{resolvedEncoding.Value.Encoding}'.",
                new InvalidOperationException("Unsupported encoding."),
                stage,
                resolvedEncoding.Value.Encoding))
        };
    }

    private TMessage DecodeMessage<TMessage>(
        ReadOnlyMemory<byte> payload,
        (EncodingKind Kind, string Encoding) resolvedEncoding,
        MessageParser<TMessage> parser,
        MessageDescriptor descriptor)
        where TMessage : class, IMessage<TMessage> => resolvedEncoding.Kind switch
        {
            EncodingKind.Binary => parser.ParseFrom(payload.Span),
            EncodingKind.Json => (TMessage)_jsonParser.Parse(System.Text.Encoding.UTF8.GetString(payload.Span), descriptor),
            _ => throw new InvalidOperationException($"Unsupported Protobuf encoding '{resolvedEncoding.Encoding}'.")
        };

    private Result<(EncodingKind Kind, string Encoding)> ResolveEncoding(string? encoding, string stage)
    {
        var effectiveEncoding = string.IsNullOrWhiteSpace(encoding) ? Encoding : encoding!;

        if (ProtobufEncoding.IsBinary(effectiveEncoding))
        {
            return Ok((EncodingKind.Binary, effectiveEncoding));
        }

        if (allowJsonEncoding && ProtobufEncoding.IsJson(effectiveEncoding))
        {
            return Ok((EncodingKind.Json, effectiveEncoding));
        }

        var error = CreateError(
            OmniRelayStatusCode.InvalidArgument,
            $"Unsupported Protobuf encoding '{effectiveEncoding}'.",
            new InvalidOperationException("Unsupported Protobuf encoding."),
            stage,
            effectiveEncoding);

        return Err<(EncodingKind, string)>(error);
    }

    private static MessageParser<TMessage> ResolveParser<TMessage>(MessageParser<TMessage>? parser)
        where TMessage : class, IMessage<TMessage>
    {
        if (parser is not null)
        {
            return parser;
        }

        var parserProperty = typeof(TMessage).GetProperty(
            "Parser",
            BindingFlags.Public |
            BindingFlags.Static);

        if (parserProperty?.GetValue(null) is MessageParser<TMessage> resolved)
        {
            return resolved;
        }

        if (typeof(TMessage).GetConstructor(Type.EmptyTypes) is not null)
        {
            return new MessageParser<TMessage>(() => Activator.CreateInstance<TMessage>()!);
        }

        throw new InvalidOperationException($"Type '{typeof(TMessage).FullName}' does not expose a parser or parameterless constructor.");
    }

    private static MessageDescriptor ResolveDescriptor<TMessage>()
        where TMessage : class, IMessage<TMessage>
    {
        var descriptorProperty = typeof(TMessage).GetProperty(
            "Descriptor",
            BindingFlags.Public |
            BindingFlags.Static);

        if (descriptorProperty?.GetValue(null) is MessageDescriptor descriptor)
        {
            return descriptor;
        }

        if (typeof(TMessage).GetConstructor(Type.EmptyTypes) is not null)
        {
            var instance = Activator.CreateInstance<TMessage>();
            if (instance is IMessage message)
            {
                return message.Descriptor;
            }
        }

        throw new InvalidOperationException($"Type '{typeof(TMessage).FullName}' does not expose a descriptor.");
    }

    private static Error CreateError(
        OmniRelayStatusCode statusCode,
        string message,
        Exception exception,
        string stage,
        string? encoding)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["encoding"] = encoding,
            ["stage"] = stage,
            ["exceptionType"] = exception.GetType().FullName,
            ["exceptionMessage"] = exception.Message
        };

        return OmniRelayErrorAdapter.FromStatus(
            statusCode,
            message,
            metadata: metadata);
    }

    private enum EncodingKind
    {
        Binary,
        Json
    }
}
