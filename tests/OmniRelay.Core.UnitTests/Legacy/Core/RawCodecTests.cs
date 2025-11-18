using OmniRelay.Core;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Core;

public class RawCodecTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_AllowsNullEncodingAndPassesThroughBuffer()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 1, 2, 3 };
        var meta = new RequestMeta(service: "echo");

        var result = codec.EncodeRequest(payload, meta);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(payload);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_MismatchedEncodingFails()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 7, 8 };
        var meta = new RequestMeta(service: "svc", procedure: "op", encoding: "json");

        var result = codec.EncodeRequest(payload, meta);

        result.IsFailure.ShouldBeTrue();
        var status = OmniRelayErrorAdapter.ToStatus(result.Error!);
        status.ShouldBe(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_ReusesUnderlyingArray()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 4, 5, 6 };
        var meta = new RequestMeta(service: "svc", encoding: "raw");

        var result = codec.DecodeRequest(new ReadOnlyMemory<byte>(payload), meta);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(payload);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeResponse_AllowsNullEncodingAndPassesThroughBuffer()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 11, 12 };
        var meta = new ResponseMeta();

        var result = codec.EncodeResponse(payload, meta);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(payload);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeResponse_MismatchedEncodingFails()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 5 };
        var meta = new ResponseMeta(encoding: "json");

        var result = codec.EncodeResponse(payload, meta);

        result.IsFailure.ShouldBeTrue();
        var status = OmniRelayErrorAdapter.ToStatus(result.Error!);
        status.ShouldBe(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeResponse_WithOffsetCopiesPayload()
    {
        var codec = new RawCodec();
        var buffer = new byte[] { 0, 9, 10, 0 };
        var slice = new ReadOnlyMemory<byte>(buffer, 1, 2);
        var meta = new ResponseMeta(encoding: "raw");

        var result = codec.DecodeResponse(slice, meta);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeSameAs(buffer);
        result.Value.ShouldBe(new byte[] { 9, 10 });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeResponse_MismatchedEncodingFails()
    {
        var codec = new RawCodec();
        var payload = new ReadOnlyMemory<byte>([1]);
        var meta = new ResponseMeta(encoding: "json");

        var result = codec.DecodeResponse(payload, meta);

        result.IsFailure.ShouldBeTrue();
        var status = OmniRelayErrorAdapter.ToStatus(result.Error!);
        status.ShouldBe(OmniRelayStatusCode.InvalidArgument);
    }
}
