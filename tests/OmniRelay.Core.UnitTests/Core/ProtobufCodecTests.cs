using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using OmniRelay.Core;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class ProtobufCodecTests
{
    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeResponse_ReturnsError_WhenJsonEncodingDisabled()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: false);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };

        var result = codec.DecodeResponse("\"noop\""u8.ToArray(), meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-response", error.Metadata["stage"]);
        Assert.Equal(ProtobufEncoding.ApplicationJson, error.Metadata["encoding"]);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Constructor_AllowsCustomDefaultEncoding()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(defaultEncoding: ProtobufEncoding.ApplicationJson);
        Assert.Equal(ProtobufEncoding.ApplicationJson, codec.Encoding);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_GeneralExceptionReturnsInternal()
    {
        var codec = new ProtobufCodec<ThrowingMessage, ThrowingMessage>();
        var meta = new RequestMeta(service: "svc", procedure: "p", encoding: ProtobufEncoding.Protobuf);

        var result = codec.EncodeRequest(new ThrowingMessage(), meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("encode-request", error.Metadata["stage"]);
        Assert.Equal(ProtobufEncoding.Protobuf, error.Metadata["encoding"]);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_GeneralExceptionReturnsInternal()
    {
        var codec = new ProtobufCodec<ThrowingMessage, ThrowingMessage>();
        var meta = new RequestMeta(service: "svc", procedure: "p", encoding: ProtobufEncoding.Protobuf);

        var result = codec.DecodeRequest(new byte[] { 0x01 }, meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-request", error.Metadata["stage"]);
        Assert.Equal(ProtobufEncoding.Protobuf, error.Metadata["encoding"]);
        Assert.Equal(typeof(InvalidProtocolBufferException).FullName, error.Metadata["exceptionType"]);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeResponse_RejectsJsonWhenDisabled()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: false);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };

        var result = codec.EncodeResponse(new StringValue { Value = "test" }, meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("encode-response", error.Metadata["stage"]);
        Assert.Equal(ProtobufEncoding.ApplicationJson, error.Metadata["encoding"]);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeResponse_InvalidJsonReturnsInvalidArgument()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: true);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };

        var result = codec.DecodeResponse("{invalid json"u8.ToArray(), meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-response", error.Metadata["stage"]);
        Assert.Equal(ProtobufEncoding.ApplicationJson, error.Metadata["encoding"]);
    }

    private sealed class ThrowingMessage : IMessage<ThrowingMessage>
    {
        public ThrowingMessage()
        {
        }

        public ThrowingMessage(ThrowingMessage other)
        {
        }

        public ThrowingMessage Clone() => new ThrowingMessage(this);

        public bool Equals(ThrowingMessage? other) => ReferenceEquals(this, other);

        public override bool Equals(object? obj) => obj is ThrowingMessage;

        public override int GetHashCode() => 0;

        MessageDescriptor IMessage.Descriptor => StringValue.Descriptor;

        public int CalculateSize() => 0;

        public void MergeFrom(ThrowingMessage message)
        {
        }

        public void MergeFrom(CodedInputStream input) => throw new Exception("merge failure");

        public void WriteTo(CodedOutputStream output) => throw new Exception("write failure");
    }
}
