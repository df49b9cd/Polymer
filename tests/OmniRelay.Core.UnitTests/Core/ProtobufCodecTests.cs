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
        encoded.IsSuccess.ShouldBeTrue();
        encoded.Value.ShouldNotBeEmpty();

        var decoded = codec.DecodeRequest(encoded.Value, meta);
        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.Value.ShouldBe(payload.Value);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_ReturnsError_WhenValueNull()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc");

        var result = codec.EncodeRequest(null!, meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("encode-request");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_ReturnsError_WhenEncodingUnsupported()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: false);
        var meta = new RequestMeta(service: "svc") { Encoding = "xml" };

        var result = codec.DecodeRequest(ReadOnlyMemory<byte>.Empty, meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("decode-request");
        error.Metadata["encoding"].ShouldBe("xml");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_ReturnsInvalidArgument_WhenPayloadCorrupted()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc") { Encoding = ProtobufEncoding.Protobuf };
        var payload = new byte[] { 1, 2, 3 };

        var result = codec.DecodeRequest(payload, meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("decode-request");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeDecodeResponse_SupportsJsonEncoding()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: true);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };
        var payload = new StringValue { Value = "world" };

        var encoded = codec.EncodeResponse(payload, meta);
        encoded.IsSuccess.ShouldBeTrue();
        Encoding.UTF8.GetString(encoded.Value).ShouldStartWith("\"");

        var decoded = codec.DecodeResponse(encoded.Value, meta);
        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.Value.ShouldBe(payload.Value);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeResponse_ReturnsError_WhenJsonEncodingDisabled()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: false);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };

        var result = codec.DecodeResponse("\"noop\""u8.ToArray(), meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("decode-response");
        error.Metadata["encoding"].ShouldBe(ProtobufEncoding.ApplicationJson);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Constructor_AllowsCustomDefaultEncoding()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(defaultEncoding: ProtobufEncoding.ApplicationJson);
        codec.Encoding.ShouldBe(ProtobufEncoding.ApplicationJson);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_GeneralExceptionReturnsInternal()
    {
        var codec = new ProtobufCodec<ThrowingMessage, ThrowingMessage>();
        var meta = new RequestMeta(service: "svc", procedure: "p", encoding: ProtobufEncoding.Protobuf);

        var result = codec.EncodeRequest(new ThrowingMessage(), meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.Internal);
        error.Metadata["stage"].ShouldBe("encode-request");
        error.Metadata["encoding"].ShouldBe(ProtobufEncoding.Protobuf);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_GeneralExceptionReturnsInternal()
    {
        var codec = new ProtobufCodec<ThrowingMessage, ThrowingMessage>();
        var meta = new RequestMeta(service: "svc", procedure: "p", encoding: ProtobufEncoding.Protobuf);

        var result = codec.DecodeRequest(new byte[] { 0x01 }, meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("decode-request");
        error.Metadata["encoding"].ShouldBe(ProtobufEncoding.Protobuf);
        error.Metadata["exceptionType"].ShouldBe(typeof(InvalidProtocolBufferException).FullName);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeResponse_RejectsJsonWhenDisabled()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: false);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };

        var result = codec.EncodeResponse(new StringValue { Value = "test" }, meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("encode-response");
        error.Metadata["encoding"].ShouldBe(ProtobufEncoding.ApplicationJson);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeResponse_InvalidJsonReturnsInvalidArgument()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>(allowJsonEncoding: true);
        var meta = new ResponseMeta { Encoding = ProtobufEncoding.ApplicationJson };

        var result = codec.DecodeResponse("{invalid json"u8.ToArray(), meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("decode-response");
        error.Metadata["encoding"].ShouldBe(ProtobufEncoding.ApplicationJson);
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
