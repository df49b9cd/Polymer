using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.Core.Leadership;

namespace OmniRelay.Core.Diagnostics;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LeadershipSnapshot))]
[JsonSerializable(typeof(LeadershipEvent))]
public sealed partial class DiagnosticsControlPlaneJsonContext : JsonSerializerContext;
