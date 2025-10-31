using System;
using Hugo;

namespace Polymer.Core;

public interface ICodec<TRequest, TResponse>
{
    string Encoding { get; }

    Result<byte[]> EncodeRequest(TRequest value, RequestMeta meta);
    Result<TRequest> DecodeRequest(ReadOnlyMemory<byte> payload, RequestMeta meta);

    Result<byte[]> EncodeResponse(TResponse value, ResponseMeta meta);
    Result<TResponse> DecodeResponse(ReadOnlyMemory<byte> payload, ResponseMeta meta);
}
