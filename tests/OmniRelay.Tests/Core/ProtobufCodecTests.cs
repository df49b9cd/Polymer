using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Tests.Core;

public class ProtobufCodecTests
{
    [Fact]
    public void EncodeRequest_Binary_SerializesMessage()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc", procedure: "echo", encoding: ProtobufEncoding.Protobuf);
        var message = new StringValue { Value = "hello" };

        var result = codec.EncodeRequest(message, meta);

        Assert.True(result.IsSuccess);
        var decoded = StringValue.Parser.ParseFrom(result.Value);
        Assert.Equal("hello", decoded.Value);
    }

    [Fact]
    public void DecodeResponse_Binary_DeserializesMessage()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new ResponseMeta(encoding: ProtobufEncoding.ApplicationProtobuf);
        var payload = new StringValue { Value = "pong" }.ToByteArray();

        var result = codec.DecodeResponse(payload, meta);

        Assert.True(result.IsSuccess);
        Assert.Equal("pong", result.Value.Value);
    }

    [Fact]
    public void EncodeRequest_Json_UsesJsonFormatter()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc", procedure: "echo", encoding: ProtobufEncoding.ApplicationJson);
        var message = new StringValue { Value = "json" };

        var result = codec.EncodeRequest(message, meta);

        Assert.True(result.IsSuccess);
        var json = Encoding.UTF8.GetString(result.Value);
        Assert.Equal("\"json\"", json);
    }

    [Fact]
    public void DecodeRequest_Json_ParsesPayload()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc", procedure: "echo", encoding: ProtobufEncoding.ApplicationJson);
        var payload = "\"decoded\""u8.ToArray();

        var result = codec.DecodeRequest(payload, meta);

        Assert.True(result.IsSuccess);
        Assert.Equal("decoded", result.Value.Value);
    }

    [Fact]
    public void EncodeRequest_UnsupportedEncoding_ReturnsError()
    {
        var codec = new ProtobufCodec<StringValue, StringValue>();
        var meta = new RequestMeta(service: "svc", procedure: "echo", encoding: "unsupported/encoding");
        var message = new StringValue();

        var result = codec.EncodeRequest(message, meta);

        Assert.True(result.IsFailure);
    }
}
