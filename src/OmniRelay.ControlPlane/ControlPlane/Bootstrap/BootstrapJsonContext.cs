using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniRelay.ControlPlane.Bootstrap;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BootstrapJoinRequest))]
[JsonSerializable(typeof(BootstrapJoinResponse))]
[JsonSerializable(typeof(BootstrapErrorResponse))]
public sealed partial class BootstrapJsonContext : JsonSerializerContext
{
}
