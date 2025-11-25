using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class RawCodecTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_AllowsNullEncoding()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 1, 2, 3 };
        var meta = new RequestMeta(service: "svc");

        var result = codec.EncodeRequest(payload, meta);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(payload);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_FailsWhenEncodingDoesNotMatch()
    {
        var codec = new RawCodec();
        var meta = new RequestMeta(service: "svc") { Encoding = "json", Procedure = "rpc" };
        var payload = new byte[] { 4 };

        var result = codec.EncodeRequest(payload, meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("encode-request");
        error.Metadata["context"].ShouldBe("request");
        error.Metadata["procedure"].ShouldBe("rpc");
        error.Metadata["expectedEncoding"].ShouldBe(RawCodec.DefaultEncoding);
        error.Metadata["actualEncoding"].ShouldBe("json");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_ReusesUnderlyingArray_WhenSegmentCoversWholeBuffer()
    {
        var codec = new RawCodec();
        var buffer = new byte[] { 9, 8, 7 };
        var payload = new ReadOnlyMemory<byte>(buffer);
        var meta = new RequestMeta(service: "svc");

        var result = codec.DecodeRequest(payload, meta);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(buffer);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_CopiesSegment_WhenArrayOffsetPresent()
    {
        var codec = new RawCodec();
        var buffer = new byte[] { 1, 2, 3, 4 };
        var payload = new ReadOnlyMemory<byte>(buffer, 1, 2);
        var meta = new RequestMeta(service: "svc");

        var result = codec.DecodeRequest(payload, meta);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeSameAs(buffer);
        result.Value.ShouldBe([2, 3]);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeResponse_ReturnsEmptyArray_WhenValueNull()
    {
        var codec = new RawCodec();
        var meta = new ResponseMeta { Encoding = RawCodec.DefaultEncoding };

        var result = codec.EncodeResponse(null!, meta);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeResponse_FailsWhenEncodingMismatch()
    {
        var codec = new RawCodec("custom");
        var meta = new ResponseMeta { Encoding = "unexpected" };

        var result = codec.DecodeResponse(new byte[] { 1, 1 }, meta);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        error.Metadata["stage"].ShouldBe("decode-response");
        error.Metadata["context"].ShouldBe("response");
        error.Metadata["expectedEncoding"].ShouldBe("custom");
        error.Metadata["actualEncoding"].ShouldBe("unexpected");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Constructor_ThrowsForInvalidEncoding()
    {
        Should.Throw<ArgumentException>(() => new RawCodec(" "));
    }
}
