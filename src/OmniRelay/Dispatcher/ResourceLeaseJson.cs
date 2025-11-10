using System.Text.Json;
using System.Text.Json.Serialization;
using Hugo;

namespace OmniRelay.Dispatcher;

public static class ResourceLeaseJson
{
    public static ResourceLeaseJsonContext Context { get; } = ResourceLeaseJsonContext.Default;
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
[JsonSerializable(typeof(Error))]
[JsonSerializable(typeof(ResourceLeaseBackpressureSignal))]
[JsonSerializable(typeof(ResourceLeaseWorkItem))]
public sealed partial class ResourceLeaseJsonContext : JsonSerializerContext;
