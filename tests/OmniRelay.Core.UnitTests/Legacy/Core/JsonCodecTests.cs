using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using OmniRelay.Core;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Core;

public class JsonCodecTests
{
    internal sealed record Sample(string Name, int Count, string? Description = null);

    [Fact]
    public void EncodeRequest_ProducesUtf8Json()
    {
        var codec = new JsonCodec<Sample, Sample>();
        var request = new RequestMeta(service: "svc", procedure: "echo");

        var result = codec.EncodeRequest(new Sample("alpha", 3), request);

        result.IsSuccess.ShouldBeTrue();
        using var document = JsonDocument.Parse(result.Value);
        document.RootElement.GetProperty("name").GetString().ShouldBe("alpha");
        document.RootElement.GetProperty("count").GetInt32().ShouldBe(3);
        document.RootElement.TryGetProperty("description", out _).ShouldBeFalse();
    }

    [Fact]
    public void DecodeRequest_InvalidJsonMapsToInvalidArgument()
    {
        var codec = new JsonCodec<Sample, Sample>();
        var payload = new ReadOnlyMemory<byte>("{ invalid json"u8.ToArray());
        var meta = new RequestMeta(service: "svc", procedure: "echo");

        var result = codec.DecodeRequest(payload, meta);

        result.IsFailure.ShouldBeTrue();
        var status = OmniRelayErrorAdapter.ToStatus(result.Error!);
        status.ShouldBe(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact]
    public void DecodeResponse_RehydratesPayload()
    {
        var codec = new JsonCodec<Sample, Sample>();
        var response = new Sample("beta", 9);
        var meta = new ResponseMeta(encoding: "json");

        var encoded = codec.EncodeResponse(response, meta);
        encoded.IsSuccess.ShouldBeTrue();

        var decoded = codec.DecodeResponse(encoded.Value, meta);

        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.ShouldBe(response);
    }

    [Fact]
    public void EncodeRequest_WithCustomOptionsIncludesNulls()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        var codec = new JsonCodec<Sample, Sample>(options);
        var request = new RequestMeta(service: "svc", procedure: "echo");

        var result = codec.EncodeRequest(new Sample("alpha", 3), request);

        result.IsSuccess.ShouldBeTrue();
        using var document = JsonDocument.Parse(result.Value);
        document.RootElement.TryGetProperty("description", out var description).ShouldBeTrue();
        description.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void DecodeRequest_WithSchemaViolation_ReturnsInvalidArgument()
    {
        var schema = JsonSchema.FromText("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "count": { "type": "integer" }
          },
          "required": ["name", "count"]
        }
        """);

        var codec = new JsonCodec<Sample, Sample>(requestSchema: schema, requestSchemaId: "sample-request");
        var payload = new ReadOnlyMemory<byte>("{\"count\":5}"u8.ToArray());
        var meta = new RequestMeta(service: "svc", procedure: "echo");

        var result = codec.DecodeRequest(payload, meta);

        result.IsFailure.ShouldBeTrue();
        var status = OmniRelayErrorAdapter.ToStatus(result.Error!);
        status.ShouldBe(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact]
    public void EncodeResponse_WithSchemaViolation_ReturnsError()
    {
        var schema = JsonSchema.FromText("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "count": { "type": "integer", "minimum": 1 }
          },
          "required": ["name", "count"]
        }
        """);

        var codec = new JsonCodec<Sample, Sample>(responseSchema: schema, responseSchemaId: "sample-response");
        var meta = new ResponseMeta(encoding: "json");

        var result = codec.EncodeResponse(new Sample("alpha", 0), meta);

        result.IsFailure.ShouldBeTrue();
        var status = OmniRelayErrorAdapter.ToStatus(result.Error!);
        status.ShouldBe(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact]
    public void Constructor_WithContextMissingTypes_Throws()
    {
        var context = new JsonCodecIncompleteContext(new JsonSerializerOptions());

        Should.Throw<InvalidOperationException>(() => new JsonCodec<Sample, Sample>(serializerContext: context));
    }
}

[JsonSerializable(typeof(JsonCodecTests.Sample))]
internal partial class JsonCodecSampleContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(string))]
internal partial class JsonCodecIncompleteContext : JsonSerializerContext
{
}
