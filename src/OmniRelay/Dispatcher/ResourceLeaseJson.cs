using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniRelay.Dispatcher;

internal static class ResourceLeaseJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
}
