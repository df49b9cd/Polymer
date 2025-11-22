using System.Text.Json.Serialization;

namespace OmniRelay.Diagnostics;

[JsonSerializable(typeof(IReadOnlyCollection<ProbeExecutionSnapshot>))]
internal sealed partial class ProbeDiagnosticsJsonContext : JsonSerializerContext;
