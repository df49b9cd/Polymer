using System.Text;
using Google.Protobuf.WellKnownTypes;
using OmniRelay.Core;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class ProtobufCodecTests
{
    [Fact]
    public void EncodeDecodeRequest_UsesBinaryEncodingByDefault()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc");
        var payload = new StringValue { Value = "hello" };

        var encoded = codec.EncodeRequest(payload, meta);
        Assert.True(encoded.IsSuccess);
        Assert.NotEmpty(encoded.Value);

        var decoded = codec.DecodeRequest(encoded.Value, meta);
        Assert.True(decoded.IsSuccess);
        Assert.Equal(payload.Value, decoded.Value.Value);
    }

    [Fact]
    public void EncodeRequest_ReturnsError_WhenValueNull()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc");

        var result = codec.EncodeRequest(null!, meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("encode-request", error.Metadata["stage"]);
    }

    [Fact]
    public void DecodeRequest_ReturnsError_WhenEncodingUnsupported()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: false);
        var meta = new RequestMeta(service: "svc") { Encoding = "xml" };

        var result = codec.DecodeRequest(ReadOnlyMemory<byte>.Empty, meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-request", error.Metadata["stage"]);
        Assert.Equal("xml", error.Metadata["encoding"]);
    }

    [Fact]
    public void DecodeRequest_ReturnsInvalidArgument_WhenPayloadCorrupted()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc") { Encoding = ProtobufEncoding.Protobuf };
        var payload = new byte[] { 1, 2, 3 };

        var result = codec.DecodeRequest(payload, meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-request", error.Metadata["stage"]);
    }

    [Fact]
    public void EncodeDecodeResponse_SupportsJsonEncoding()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: true);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };
        var payload = new StringValue { Value = "world" };

        var encoded = codec.EncodeResponse(payload, meta);
        Assert.True(encoded.IsSuccess);
        Assert.StartsWith("\"", Encoding.UTF8.GetString(encoded.Value), StringComparison.Ordinal);

        var decoded = codec.DecodeResponse(encoded.Value, meta);
        Assert.True(decoded.IsSuccess);
        Assert.Equal(payload.Value, decoded.Value.Value);
    }

    [Fact]
    public void DecodeResponse_ReturnsError_WhenJsonEncodingDisabled()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: false);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };

        var result = codec.DecodeResponse(Encoding.UTF8.GetBytes("\"noop\""), meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-response", error.Metadata["stage"]);
        Assert.Equal(ProtobufEncoding.ApplicationJson, error.Metadata["encoding"]);
    }

    [Fact]
    public void Constructor_AllowsCustomDefaultEncoding()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(defaultEncoding: ProtobufEncoding.ApplicationJson);
        Assert.Equal(ProtobufEncoding.ApplicationJson, codec.Encoding);
    }
}
