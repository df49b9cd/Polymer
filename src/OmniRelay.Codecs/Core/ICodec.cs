using Hugo;

namespace OmniRelay.Core;

/// <summary>
/// Contract for encoding and decoding typed request/response messages to raw bytes.
/// </summary>
public interface ICodec<TRequest, TResponse>
{
    /// <summary>
    /// Gets the encoding identifier (e.g., json, protobuf, raw).
    /// </summary>
    string Encoding { get; }

    /// <summary>Encodes a typed request value into a transport payload.</summary>
    /// <param name="value">The request value.</param>
    /// <param name="meta">The request metadata.</param>
    /// <returns>Encoded bytes or an error.</returns>
    Result<byte[]> EncodeRequest(TRequest value, RequestMeta meta);
    /// <summary>Decodes a transport payload into a typed request value.</summary>
    Result<TRequest> DecodeRequest(ReadOnlyMemory<byte> payload, RequestMeta meta);

    /// <summary>Encodes a typed response value into a transport payload.</summary>
    Result<byte[]> EncodeResponse(TResponse value, ResponseMeta meta);
    /// <summary>Decodes a transport payload into a typed response value.</summary>
    Result<TResponse> DecodeResponse(ReadOnlyMemory<byte> payload, ResponseMeta meta);
}
