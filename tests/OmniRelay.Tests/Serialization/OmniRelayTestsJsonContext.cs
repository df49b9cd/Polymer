using System.Text.Json.Serialization;
using OmniRelay.Tests.Dispatcher;
using OmniRelay.Tests.Transport.Http;

namespace OmniRelay.Tests;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonEchoRequest))]
[JsonSerializable(typeof(JsonEchoResponse))]
[JsonSerializable(typeof(MetaDiagnosticsPayload))]
internal partial class OmniRelayTestsJsonContext : JsonSerializerContext;
