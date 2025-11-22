using System.Text.Json.Serialization;

namespace OmniRelay.Core.Gossip;

/// <summary>Source-generated serializer context for gossip payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MeshGossipEnvelope))]
[JsonSerializable(typeof(MeshGossipMemberSnapshot))]
[JsonSerializable(typeof(MeshGossipClusterView))]
[JsonSerializable(typeof(MeshGossipShuffleEnvelope))]
internal partial class MeshGossipJsonSerializerContext : JsonSerializerContext;
