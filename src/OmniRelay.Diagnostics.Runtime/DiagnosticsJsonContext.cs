using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniRelay.Diagnostics;

public sealed record LoggingStateResponse(string? MinimumLevel);

public sealed record TraceSamplingResponse(double? SamplingProbability);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LoggingStateResponse))]
[JsonSerializable(typeof(TraceSamplingResponse))]
[JsonSerializable(typeof(PeerLeaseHealthDiagnostics))]
[JsonSerializable(typeof(PeerDiagnosticsResponse))]
[JsonSerializable(typeof(DiagnosticsLogLevelRequest))]
[JsonSerializable(typeof(DiagnosticsSamplingRequest))]
public sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
