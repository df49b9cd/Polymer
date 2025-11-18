using System.Text.Json.Serialization;

namespace OmniRelay.Diagnostics;

public sealed record LoggingStateResponse(string? MinimumLevel);

public sealed record TraceSamplingResponse(double? SamplingProbability);

[JsonSerializable(typeof(LoggingStateResponse))]
[JsonSerializable(typeof(TraceSamplingResponse))]
[JsonSerializable(typeof(PeerLeaseHealthDiagnostics))]
[JsonSerializable(typeof(PeerDiagnosticsResponse))]
public sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
