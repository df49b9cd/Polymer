using System.Text.Json.Serialization;
using OmniRelay.Tests.Dispatcher;

namespace OmniRelay.Tests;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonEchoRequest))]
[JsonSerializable(typeof(JsonEchoResponse))]
internal partial class OmniRelayTestsJsonContext : JsonSerializerContext;
