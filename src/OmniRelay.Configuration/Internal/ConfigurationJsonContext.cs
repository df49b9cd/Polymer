using System.Text.Json.Serialization;
using OmniRelay.Configuration.Models;

namespace OmniRelay.Configuration.Internal;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BootstrapPolicyDocumentConfiguration))]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext;
