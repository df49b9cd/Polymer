using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Diagnostics;

namespace OmniRelay.Core.Diagnostics;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(NodeDrainSnapshot))]
[JsonSerializable(typeof(PeerDiagnosticsResponse))]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext
{
}
