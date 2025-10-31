using System;
using System.Collections.Generic;
using System.Text.Json;
using Hugo;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core;

public sealed class JsonCodec<TRequest, TResponse> : ICodec<TRequest, TResponse>
{
    private readonly JsonSerializerOptions _options;

    public JsonCodec(JsonSerializerOptions? options = null, string encoding = "json")
    {
        _options = options ?? CreateDefaultOptions();
        Encoding = encoding;
    }

    public string Encoding { get; }

    public Result<byte[]> EncodeRequest(TRequest value, RequestMeta meta)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            return Ok(bytes);
        }
        catch (Exception ex)
        {
            return Err<byte[]>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                $"Failed to encode request for procedure '{meta.Procedure ?? "unknown"}'.",
                metadata: BuildExceptionMetadata(ex, "encode-request")));
        }
    }

    public Result<TRequest> DecodeRequest(ReadOnlyMemory<byte> payload, RequestMeta meta)
    {
        try
        {
            var value = JsonSerializer.Deserialize<TRequest>(payload.Span, _options);
            return Ok(value!);
        }
        catch (JsonException ex)
        {
            return Err<TRequest>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.InvalidArgument,
                $"Failed to decode request for procedure '{meta.Procedure ?? "unknown"}'.",
                metadata: BuildExceptionMetadata(ex, "decode-request")));
        }
        catch (Exception ex)
        {
            return Err<TRequest>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                $"Unexpected error while decoding request for procedure '{meta.Procedure ?? "unknown"}'.",
                metadata: BuildExceptionMetadata(ex, "decode-request")));
        }
    }

    public Result<byte[]> EncodeResponse(TResponse value, ResponseMeta meta)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            return Ok(bytes);
        }
        catch (Exception ex)
        {
            return Err<byte[]>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                "Failed to encode response payload.",
                metadata: BuildExceptionMetadata(ex, "encode-response")));
        }
    }

    public Result<TResponse> DecodeResponse(ReadOnlyMemory<byte> payload, ResponseMeta meta)
    {
        try
        {
            var value = JsonSerializer.Deserialize<TResponse>(payload.Span, _options);
            return Ok(value!);
        }
        catch (JsonException ex)
        {
            return Err<TResponse>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.InvalidArgument,
                "Failed to decode response payload.",
                metadata: BuildExceptionMetadata(ex, "decode-response")));
        }
        catch (Exception ex)
        {
            return Err<TResponse>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                "Unexpected error while decoding response payload.",
                metadata: BuildExceptionMetadata(ex, "decode-response")));
        }
    }

    private static JsonSerializerOptions CreateDefaultOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

    private IReadOnlyDictionary<string, object?> BuildExceptionMetadata(Exception exception, string stage)
    {
        return new Dictionary<string, object?>
        {
            ["encoding"] = Encoding,
            ["stage"] = stage,
            ["exceptionType"] = exception.GetType().FullName,
            ["exceptionMessage"] = exception.Message
        };
    }
}
