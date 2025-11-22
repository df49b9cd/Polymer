using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.Transport.Security;

namespace OmniRelay.Transport.Http;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TransportSecurityDecisionPayload))]
[JsonSerializable(typeof(TransportAuthorizationResponse))]
internal sealed partial class HttpJsonContext : JsonSerializerContext
{
}
