using System.Runtime.InteropServices;
using Hugo;
using YARPCore.Errors;
using static Hugo.Go;

namespace YARPCore.Core;

/// <summary>
/// Passthrough codec for raw binary payloads. Enforces that request/response metadata
/// declares the expected encoding (when specified) and avoids unnecessary copies when
/// the underlying buffer can be re-used.
/// </summary>
public sealed class RawCodec : ICodec<byte[], byte[]>
{
    private readonly StringComparer _comparer = StringComparer.OrdinalIgnoreCase;

    public const string DefaultEncoding = "raw";

    public RawCodec(string encoding = DefaultEncoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            throw new ArgumentException("Encoding identifier cannot be null or whitespace.", nameof(encoding));
        }

        Encoding = encoding;
    }

    public string Encoding { get; }

    public Result<byte[]> EncodeRequest(byte[] value, RequestMeta meta)
    {
        if (!IsEncodingPermitted(meta.Encoding))
        {
            return FailInvalidEncoding<byte[]>(
                stage: "encode-request",
                context: "request",
                actual: meta.Encoding,
                service: meta.Service,
                procedure: meta.Procedure);
        }

        return Ok(value ?? []);
    }

    public Result<byte[]> DecodeRequest(ReadOnlyMemory<byte> payload, RequestMeta meta)
    {
        if (!IsEncodingPermitted(meta.Encoding))
        {
            return FailInvalidEncoding<byte[]>(
                stage: "decode-request",
                context: "request",
                actual: meta.Encoding,
                service: meta.Service,
                procedure: meta.Procedure);
        }

        return Ok(Normalize(payload));
    }

    public Result<byte[]> EncodeResponse(byte[] value, ResponseMeta meta)
    {
        if (!IsEncodingPermitted(meta.Encoding))
        {
            return FailInvalidEncoding<byte[]>(
                stage: "encode-response",
                context: "response",
                actual: meta.Encoding);
        }

        return Ok(value ?? []);
    }

    public Result<byte[]> DecodeResponse(ReadOnlyMemory<byte> payload, ResponseMeta meta)
    {
        if (!IsEncodingPermitted(meta.Encoding))
        {
            return FailInvalidEncoding<byte[]>(
                stage: "decode-response",
                context: "response",
                actual: meta.Encoding);
        }

        return Ok(Normalize(payload));
    }

    private bool IsEncodingPermitted(string? declaredEncoding) =>
        declaredEncoding is null || _comparer.Equals(declaredEncoding, Encoding);

    private Result<T> FailInvalidEncoding<T>(
        string stage,
        string context,
        string? actual,
        string? service = null,
        string? procedure = null)
    {
        var errorMessage = actual is null
            ? $"Raw codec requires {context} metadata encoding '{Encoding}' but no encoding was provided."
            : $"Raw codec requires {context} metadata encoding '{Encoding}' but encountered '{actual}'.";

        var metadata = new Dictionary<string, object?>
        {
            ["expectedEncoding"] = Encoding,
            ["actualEncoding"] = actual,
            ["context"] = context,
            ["stage"] = stage
        };

        if (!string.IsNullOrWhiteSpace(service))
        {
            metadata["service"] = service;
        }

        if (!string.IsNullOrWhiteSpace(procedure))
        {
            metadata["procedure"] = procedure;
        }

        var error = PolymerErrorAdapter.FromStatus(
            PolymerStatusCode.InvalidArgument,
            errorMessage,
            metadata: metadata);

        return Err<T>(error);
    }

    private static byte[] Normalize(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        if (MemoryMarshal.TryGetArray(payload, out var segment) &&
            segment.Array is { } array &&
            segment.Offset == 0 &&
            segment.Count == array.Length)
        {
            return array;
        }

        return payload.ToArray();
    }
}
