using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniRelay.Diagnostics.Alerting;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AlertEvent))]
internal sealed partial class WebhookAlertJsonContext : JsonSerializerContext
{
}
