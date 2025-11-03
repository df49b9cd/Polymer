using System;
using Polymer.Core;
using Polymer.Errors;
using Xunit;

namespace Polymer.Tests.Core;

public class RawCodecTests
{
    [Fact]
    public void EncodeRequest_AllowsNullEncodingAndPassesThroughBuffer()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 1, 2, 3 };
        var meta = new RequestMeta(service: "echo");

        var result = codec.EncodeRequest(payload, meta);

        Assert.True(result.IsSuccess);
        Assert.Same(payload, result.Value);
    }

    [Fact]
    public void EncodeRequest_MismatchedEncodingFails()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 7, 8 };
        var meta = new RequestMeta(service: "svc", procedure: "op", encoding: "json");

        var result = codec.EncodeRequest(payload, meta);

        Assert.True(result.IsFailure);
        var status = PolymerErrorAdapter.ToStatus(result.Error!);
        Assert.Equal(PolymerStatusCode.InvalidArgument, status);
    }

    [Fact]
    public void DecodeRequest_ReusesUnderlyingArray()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 4, 5, 6 };
        var meta = new RequestMeta(service: "svc", encoding: "raw");

        var result = codec.DecodeRequest(new ReadOnlyMemory<byte>(payload), meta);

        Assert.True(result.IsSuccess);
        Assert.Same(payload, result.Value);
    }

    [Fact]
    public void EncodeResponse_AllowsNullEncodingAndPassesThroughBuffer()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 11, 12 };
        var meta = new ResponseMeta();

        var result = codec.EncodeResponse(payload, meta);

        Assert.True(result.IsSuccess);
        Assert.Same(payload, result.Value);
    }

    [Fact]
    public void EncodeResponse_MismatchedEncodingFails()
    {
        var codec = new RawCodec();
        var payload = new byte[] { 5 };
        var meta = new ResponseMeta(encoding: "json");

        var result = codec.EncodeResponse(payload, meta);

        Assert.True(result.IsFailure);
        var status = PolymerErrorAdapter.ToStatus(result.Error!);
        Assert.Equal(PolymerStatusCode.InvalidArgument, status);
    }

    [Fact]
    public void DecodeResponse_WithOffsetCopiesPayload()
    {
        var codec = new RawCodec();
        var buffer = new byte[] { 0, 9, 10, 0 };
        var slice = new ReadOnlyMemory<byte>(buffer, 1, 2);
        var meta = new ResponseMeta(encoding: "raw");

        var result = codec.DecodeResponse(slice, meta);

        Assert.True(result.IsSuccess);
        Assert.NotSame(buffer, result.Value);
        Assert.Equal(new byte[] { 9, 10 }, result.Value);
    }

    [Fact]
    public void DecodeResponse_MismatchedEncodingFails()
    {
        var codec = new RawCodec();
        var payload = new ReadOnlyMemory<byte>(new byte[] { 1 });
        var meta = new ResponseMeta(encoding: "json");

        var result = codec.DecodeResponse(payload, meta);

        Assert.True(result.IsFailure);
        var status = PolymerErrorAdapter.ToStatus(result.Error!);
        Assert.Equal(PolymerStatusCode.InvalidArgument, status);
    }
}
