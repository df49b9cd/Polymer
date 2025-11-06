using OmniRelay.Core;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class RawCodecTests
{
    [Fact]
    public void EncodeRequest_AllowsNullEncoding()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 1, 2, 3 };
        var meta = new RequestMeta(service: "svc");

        var result = codec.EncodeRequest(payload, meta);

        Assert.True(result.IsSuccess);
        Assert.Same(payload, result.Value);
    }

    [Fact]
    public void EncodeRequest_FailsWhenEncodingDoesNotMatch()
    {
        var codec = new RawCodec();
        var meta = new RequestMeta(service: "svc") { Encoding = "json", Procedure = "rpc" };
        var payload = new byte[] { 4 };

        var result = codec.EncodeRequest(payload, meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("encode-request", error.Metadata["stage"]);
        Assert.Equal("request", error.Metadata["context"]);
        Assert.Equal("rpc", error.Metadata["procedure"]);
        Assert.Equal(RawCodec.DefaultEncoding, error.Metadata["expectedEncoding"]);
        Assert.Equal("json", error.Metadata["actualEncoding"]);
    }

    [Fact]
    public void DecodeRequest_ReusesUnderlyingArray_WhenSegmentCoversWholeBuffer()
    {
        var codec = new RawCodec();
        var buffer = new byte[] { 9, 8, 7 };
        var payload = new ReadOnlyMemory<byte>(buffer);
        var meta = new RequestMeta(service: "svc");

        var result = codec.DecodeRequest(payload, meta);

        Assert.True(result.IsSuccess);
        Assert.Same(buffer, result.Value);
    }

    [Fact]
    public void DecodeRequest_CopiesSegment_WhenArrayOffsetPresent()
    {
        var codec = new RawCodec();
        var buffer = new byte[] { 1, 2, 3, 4 };
        var payload = new ReadOnlyMemory<byte>(buffer, 1, 2);
        var meta = new RequestMeta(service: "svc");

        var result = codec.DecodeRequest(payload, meta);

        Assert.True(result.IsSuccess);
        Assert.NotSame(buffer, result.Value);
        Assert.Equal(new byte[] { 2, 3 }, result.Value);
    }

    [Fact]
    public void EncodeResponse_ReturnsEmptyArray_WhenValueNull()
    {
        var codec = new RawCodec();
        var meta = new ResponseMeta { Encoding = RawCodec.DefaultEncoding };

        var result = codec.EncodeResponse(null!, meta);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void DecodeResponse_FailsWhenEncodingMismatch()
    {
        var codec = new RawCodec("custom");
        var meta = new ResponseMeta { Encoding = "unexpected" };

        var result = codec.DecodeResponse(new byte[] { 1, 1 }, meta);

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-response", error.Metadata["stage"]);
        Assert.Equal("response", error.Metadata["context"]);
        Assert.Equal("custom", error.Metadata["expectedEncoding"]);
        Assert.Equal("unexpected", error.Metadata["actualEncoding"]);
    }

    [Fact]
    public void Constructor_ThrowsForInvalidEncoding()
    {
        Assert.Throws<ArgumentException>(() => new RawCodec(" "));
    }
}
