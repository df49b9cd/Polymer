using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.Dispatcher;

namespace OmniRelay.Cli;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DispatcherIntrospection))]
[JsonSerializable(typeof(AutomationScript))]
[JsonSerializable(typeof(AutomationStep))]
internal partial class OmniRelayCliJsonContext : JsonSerializerContext;
