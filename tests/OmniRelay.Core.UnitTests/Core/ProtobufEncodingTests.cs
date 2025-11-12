using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class ProtobufEncodingTests
{
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("protobuf", true)]
    [InlineData("application/x-protobuf", true)]
    [InlineData("application/protobuf", true)]
    [InlineData("application/grpc", true)]
    [InlineData("application/grpc+proto", true)]
    [InlineData("json", false)]
    [InlineData("application/json", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsBinary_Works(string? encoding, bool expected)
    {
        ProtobufEncoding.IsBinary(encoding).ShouldBe(expected);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("json", true)]
    [InlineData("application/json", true)]
    [InlineData("protobuf", false)]
    [InlineData(null, false)]
    public void IsJson_Works(string? encoding, bool expected)
    {
        ProtobufEncoding.IsJson(encoding).ShouldBe(expected);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("protobuf", ProtobufEncoding.ApplicationProtobuf)]
    [InlineData("application/json", ProtobufEncoding.ApplicationJson)]
    [InlineData("application/xml", "application/xml")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void GetMediaType_Maps(string? input, string? expected)
    {
        ProtobufEncoding.GetMediaType(input).ShouldBe(expected);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("application/json", ProtobufEncoding.ApplicationJson)]
    [InlineData("json", ProtobufEncoding.ApplicationJson)]
    [InlineData("application/grpc", ProtobufEncoding.Protobuf)]
    [InlineData("protobuf", ProtobufEncoding.Protobuf)]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Normalize_Works(string? input, string? expected)
    {
        ProtobufEncoding.Normalize(input).ShouldBe(expected);
    }
}
