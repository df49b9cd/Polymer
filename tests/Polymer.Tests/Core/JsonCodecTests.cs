using System;
using System.Text;
using System.Text.Json;
using Polymer.Core;
using Polymer.Errors;
using Xunit;

namespace Polymer.Tests.Core;

public class JsonCodecTests
{
    private sealed record Sample(string Name, int Count);

    [Fact]
    public void EncodeRequest_ProducesUtf8Json()
    {
        var codec = new JsonCodec<Sample, Sample>();
        var request = new RequestMeta(service: "svc", procedure: "echo");

        var result = codec.EncodeRequest(new Sample("alpha", 3), request);

        Assert.True(result.IsSuccess);
        using var document = JsonDocument.Parse(result.Value);
        Assert.Equal("alpha", document.RootElement.GetProperty("name").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void DecodeRequest_InvalidJsonMapsToInvalidArgument()
    {
        var codec = new JsonCodec<Sample, Sample>();
        var payload = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{ invalid json"));
        var meta = new RequestMeta(service: "svc", procedure: "echo");

        var result = codec.DecodeRequest(payload, meta);

        Assert.True(result.IsFailure);
        var status = PolymerErrorAdapter.ToStatus(result.Error!);
        Assert.Equal(PolymerStatusCode.InvalidArgument, status);
    }

    [Fact]
    public void DecodeResponse_RehydratesPayload()
    {
        var codec = new JsonCodec<Sample, Sample>();
        var response = new Sample("beta", 9);
        var meta = new ResponseMeta(encoding: "json");

        var encoded = codec.EncodeResponse(response, meta);
        Assert.True(encoded.IsSuccess);

        var decoded = codec.DecodeResponse(encoded.Value, meta);

        Assert.True(decoded.IsSuccess);
        Assert.Equal(response, decoded.Value);
    }
}
