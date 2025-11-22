using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core;

/// <summary>
/// Protobuf codec supporting binary and optional JSON encodings with robust error mapping.
/// </summary>
public sealed class ProtobufCodec<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties)]
TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties)]
TResponse>(
    MessageParser<TRequest>? requestParser = null,
    MessageParser<TResponse>? responseParser = null,
    JsonParser? jsonParser = null,
    JsonFormatter? jsonFormatter = null,
    string? defaultEncoding = null,
    bool allowJsonEncoding = true)
    : ICodec<TRequest, TResponse>
    where TRequest : class, IMessage<TRequest>, new()
    where TResponse : class, IMessage<TResponse>, new()
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

        var procedure = meta.Procedure ?? "unknown";
        return EncodeMessage(
            value,
            meta.Encoding,
            EncodeRequestStage,
            () => $"Failed to encode Protobuf request for procedure '{procedure}'.",
            () => $"Unexpected error while encoding Protobuf request for procedure '{procedure}'.");
    }

    /// <inheritdoc />
    public Result<TRequest> DecodeRequest(ReadOnlyMemory<byte> payload, RequestMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var procedure = meta.Procedure ?? "unknown";
        return ResolveEncoding(meta.Encoding, DecodeRequestStage)
            .Then(descriptor => DecodeMessage(
                payload,
                descriptor,
                _requestParser,
                _requestDescriptor,
                DecodeRequestStage,
                () => $"Failed to decode Protobuf request for procedure '{procedure}'.",
                () => $"Unexpected error while decoding Protobuf request for procedure '{procedure}'.",
                meta.Encoding));
    }

    /// <inheritdoc />
    public Result<byte[]> EncodeResponse(TResponse value, ResponseMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        return EncodeMessage(
            value,
            meta.Encoding,
            EncodeResponseStage,
            () => "Failed to encode Protobuf response payload.",
            () => "Unexpected error while encoding Protobuf response payload.");
    }

    /// <inheritdoc />
    public Result<TResponse> DecodeResponse(ReadOnlyMemory<byte> payload, ResponseMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        return ResolveEncoding(meta.Encoding, DecodeResponseStage)
            .Then(descriptor => DecodeMessage(
                payload,
                descriptor,
                _responseParser,
                _responseDescriptor,
                DecodeResponseStage,
                () => "Failed to decode Protobuf response payload.",
                () => "Unexpected error while decoding Protobuf response payload.",
                meta.Encoding));
    }

    private Result<byte[]> EncodeMessage<TMessage>(
        TMessage value,
        string? encoding,
        string stage,
        Func<string> invalidMessage,
        Func<string> unexpectedMessage)
        where TMessage : class, IMessage<TMessage> =>
        EnsureValue(value, stage, invalidMessage)
            .Then(_ => ResolveEncoding(encoding, stage))
            .Then(descriptor => ConvertMessage(value, descriptor, stage, encoding, invalidMessage, unexpectedMessage));

    private Result<TMessage> DecodeMessage<TMessage>(
        ReadOnlyMemory<byte> payload,
        (EncodingKind Kind, string Encoding) resolvedEncoding,
        MessageParser<TMessage> parser,
        MessageDescriptor descriptor,
        string stage,
        Func<string> invalidMessage,
        Func<string> unexpectedMessage,
        string? requestedEncoding)
        where TMessage : class, IMessage<TMessage>
    {
        try
        {
            var result = resolvedEncoding.Kind switch
            {
                EncodingKind.Binary => parser.ParseFrom(payload.Span),
                EncodingKind.Json => (TMessage)_jsonParser.Parse(System.Text.Encoding.UTF8.GetString(payload.Span), descriptor),
                _ => throw new InvalidOperationException($"Unsupported Protobuf encoding '{resolvedEncoding.Encoding}'.")
            };

            return Ok(result);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return Err<TMessage>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                invalidMessage(),
                ex,
                stage,
                requestedEncoding ?? resolvedEncoding.Encoding));
        }
        catch (Exception ex)
        {
            return Err<TMessage>(CreateError(
                OmniRelayStatusCode.Internal,
                unexpectedMessage(),
                ex,
                stage,
                requestedEncoding ?? resolvedEncoding.Encoding));
        }
    }

    private static Result<Unit> EnsureValue<TMessage>(
        TMessage value,
        string stage,
        Func<string> invalidMessage)
        where TMessage : class =>
        value is null
            ? Err<Unit>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                invalidMessage(),
                new InvalidOperationException("Protobuf value cannot be null."),
                stage,
                null))
            : Ok(Unit.Value);

    private Result<byte[]> ConvertMessage<TMessage>(
        TMessage value,
        (EncodingKind Kind, string Encoding) resolvedEncoding,
        string stage,
        string? requestedEncoding,
        Func<string> invalidMessage,
        Func<string> unexpectedMessage)
        where TMessage : class, IMessage<TMessage>
    {
        try
        {
            return resolvedEncoding.Kind switch
            {
                EncodingKind.Binary => Ok(value!.ToByteArray()),
                EncodingKind.Json => Ok(System.Text.Encoding.UTF8.GetBytes(_jsonFormatter.Format(value!))),
                _ => Err<byte[]>(CreateError(
                    OmniRelayStatusCode.InvalidArgument,
                    invalidMessage(),
                    new InvalidOperationException("Unsupported Protobuf encoding."),
                    stage,
                    resolvedEncoding.Encoding))
            };
        }
        catch (InvalidProtocolBufferException ex)
        {
            return Err<byte[]>(CreateError(
                OmniRelayStatusCode.InvalidArgument,
                invalidMessage(),
                ex,
                stage,
                requestedEncoding ?? resolvedEncoding.Encoding));
        }
        catch (Exception ex)
        {
            return Err<byte[]>(CreateError(
                OmniRelayStatusCode.Internal,
                unexpectedMessage(),
                ex,
                stage,
                requestedEncoding ?? resolvedEncoding.Encoding));
        }
    }

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

    private static MessageParser<TMessage> ResolveParser<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties)]
    TMessage>(MessageParser<TMessage>? parser)
        where TMessage : class, IMessage<TMessage>, new() =>
        parser ?? MessageMetadata<TMessage>.Parser;

    private static MessageDescriptor ResolveDescriptor<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties)]
    TMessage>()
        where TMessage : class, IMessage<TMessage>, new() =>
        MessageMetadata<TMessage>.Descriptor;

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

    private static class MessageMetadata<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties)]
    TMessage>
        where TMessage : class, IMessage<TMessage>, new()
    {
        internal static readonly MessageParser<TMessage> Parser = new(static () => new TMessage());
        internal static readonly MessageDescriptor Descriptor = new TMessage().Descriptor;
    }
}
