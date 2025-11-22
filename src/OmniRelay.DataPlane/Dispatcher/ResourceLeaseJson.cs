using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hugo;
using OmniRelay.ControlPlane.Throttling;

namespace OmniRelay.Dispatcher;

public static class ResourceLeaseJson
{
    public static ResourceLeaseJsonContext Context { get; } = CreateContext();

    private static ResourceLeaseJsonContext CreateContext()
    {
        var baseContext = ResourceLeaseJsonContext.Default;
        var baseOptions = baseContext.Options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var deterministicResolver = DeterministicJsonSerialization.DefaultContext;

        var resolver = baseOptions.TypeInfoResolver is null
            ? deterministicResolver
            : JsonTypeInfoResolver.Combine(baseOptions.TypeInfoResolver, deterministicResolver);

        var options = new JsonSerializerOptions(baseOptions)
        {
            TypeInfoResolver = resolver
        };

        return new ResourceLeaseJsonContext(options);
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResourceLeaseReplicationEvent))]
[JsonSerializable(typeof(ResourceLeaseReplicationEvent[]))]
[JsonSerializable(typeof(ResourceLeaseItemPayload))]
[JsonSerializable(typeof(ResourceLeaseEnqueueRequest))]
[JsonSerializable(typeof(ResourceLeaseEnqueueResponse))]
[JsonSerializable(typeof(ResourceLeaseLeaseRequest))]
[JsonSerializable(typeof(ResourceLeaseLeaseResponse))]
[JsonSerializable(typeof(ResourceLeaseCompleteRequest))]
[JsonSerializable(typeof(ResourceLeaseHeartbeatRequest))]
[JsonSerializable(typeof(ResourceLeaseFailRequest))]
[JsonSerializable(typeof(ResourceLeaseDrainRequest))]
[JsonSerializable(typeof(ResourceLeaseDrainResponse))]
[JsonSerializable(typeof(ResourceLeaseRestoreRequest))]
[JsonSerializable(typeof(ResourceLeaseRestoreResponse))]
[JsonSerializable(typeof(ResourceLeaseQueueStats))]
[JsonSerializable(typeof(ResourceLeaseAcknowledgeResponse))]
[JsonSerializable(typeof(ResourceLeasePendingItemDto))]
[JsonSerializable(typeof(ResourceLeaseOwnershipHandle))]
[JsonSerializable(typeof(ResourceLeaseErrorInfo))]
[JsonSerializable(typeof(ResourceLeaseBackpressureSignal))]
[JsonSerializable(typeof(ResourceLeaseWorkItem))]
public sealed partial class ResourceLeaseJsonContext : JsonSerializerContext;
