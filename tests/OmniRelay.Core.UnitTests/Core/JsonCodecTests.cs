using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using OmniRelay.Core;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class JsonCodecTests
{
    private sealed record TestPayload(string Id, int Count)
    {
        public string Id { get; init; } = Id;

        public int Count { get; init; } = Count;
    }

    private sealed record OptionalPayload(string? Name)
    {
        public string? Name { get; init; } = Name;
    }

    private sealed class ThrowingWriteConverter : JsonConverter<TestPayload>
    {
        public override TestPayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Read should not be used in this converter.");

        public override void Write(Utf8JsonWriter writer, TestPayload value, JsonSerializerOptions options) =>
            throw new InvalidOperationException("write failed");
    }

    private sealed class ThrowingReadConverter : JsonConverter<TestPayload>
    {
        public override TestPayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new InvalidOperationException("read failed");

        public override void Write(Utf8JsonWriter writer, TestPayload value, JsonSerializerOptions options) =>
            throw new NotSupportedException("Write should not be used in this converter.");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeDecode_RoundTrips_WithDefaultOptions()
    {
        var codec = new JsonCodec<TestPayload, TestPayload>();
        var requestMeta = new RequestMeta(service: "svc") { Procedure = "proc" };
        var responseMeta = new ResponseMeta { Transport = "http" };
        var payload = new TestPayload("alpha", 7);

        var encoded = codec.EncodeRequest(payload, requestMeta);
        Assert.True(encoded.IsSuccess);
        Assert.Equal("json", codec.Encoding);

        var decoded = codec.DecodeRequest(encoded.Value, requestMeta);
        Assert.True(decoded.IsSuccess);
        Assert.Equal(payload, decoded.Value);

        var responseEncoded = codec.EncodeResponse(payload, responseMeta);
        Assert.True(responseEncoded.IsSuccess);

        var responseDecoded = codec.DecodeResponse(responseEncoded.Value, responseMeta);
        Assert.True(responseDecoded.IsSuccess);
        Assert.Equal(payload, responseDecoded.Value);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_ReportsSchemaViolations()
    {
        var schema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(("name", new JsonSchemaBuilder().Type(SchemaValueType.String)))
            .Required("name")
            .Build();

        var codec = new JsonCodec<OptionalPayload, OptionalPayload>(
            requestSchema: schema,
            requestSchemaId: "schema://json/request");

        var meta = new RequestMeta(service: "svc") { Procedure = "missing_field" };

        var result = codec.EncodeRequest(new OptionalPayload(null), meta);

        Assert.True(result.IsFailure);

        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("encode-request", error.Metadata["stage"]);
        Assert.Equal("schema://json/request", error.Metadata["schemaId"]);

        var errors = Assert.IsAssignableFrom<IReadOnlyList<string>>(error.Metadata["errors"]);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_ReturnsInvalidArgument_OnMalformedJson()
    {
        var codec = new JsonCodec<TestPayload, TestPayload>();
        var meta = new RequestMeta(service: "svc");
        var payload = "{\"id\":"u8.ToArray();

        var result = codec.DecodeRequest(payload, meta);

        Assert.True(result.IsFailure);

        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-request", error.Metadata["stage"]);
        var exceptionType = Assert.IsType<string>(error.Metadata["exceptionType"]);
        Assert.Contains("Json", exceptionType);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeResponse_ReturnsSchemaParseFailure_ForInvalidJson()
    {
        var schema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(("id", new JsonSchemaBuilder().Type(SchemaValueType.String)))
            .Build();

        var codec = new JsonCodec<TestPayload, TestPayload>(
            responseSchema: schema,
            responseSchemaId: "schema://json/response");

        var meta = new ResponseMeta { Encoding = "json", Transport = "grpc" };
        var payload = "not json at all"u8.ToArray();

        var result = codec.DecodeResponse(payload, meta);

        Assert.True(result.IsFailure);

        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-response", error.Metadata["stage"]);
        var exceptionType = Assert.IsType<string>(error.Metadata["exceptionType"]);
        Assert.Contains("Json", exceptionType);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DecodeRequest_ReturnsInternalError_WhenConverterThrows()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ThrowingReadConverter());

        var codec = new JsonCodec<TestPayload, TestPayload>(options: options);
        var meta = new RequestMeta(service: "svc");
        var payload = "{\"Id\":\"a\",\"Count\":1}"u8.ToArray();

        var result = codec.DecodeRequest(payload, meta);

        Assert.True(result.IsFailure);

        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("decode-request", error.Metadata["stage"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, error.Metadata["exceptionType"]);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EncodeRequest_ReturnsInternalError_WhenConverterThrows()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ThrowingWriteConverter());

        var codec = new JsonCodec<TestPayload, TestPayload>(options: options);
        var meta = new RequestMeta(service: "svc");

        var result = codec.EncodeRequest(new TestPayload("id", 1), meta);

        Assert.True(result.IsFailure);

        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("encode-request", error.Metadata["stage"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, error.Metadata["exceptionType"]);
    }
}
