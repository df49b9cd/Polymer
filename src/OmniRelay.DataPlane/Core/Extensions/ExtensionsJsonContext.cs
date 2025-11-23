using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.Diagnostics;

namespace OmniRelay.Core.Extensions;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ExtensionDiagnosticsResponse))]
internal sealed partial class ExtensionsJsonContext : JsonSerializerContext
{
}
