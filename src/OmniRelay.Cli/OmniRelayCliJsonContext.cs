using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Gossip;
using OmniRelay.Dispatcher;

namespace OmniRelay.Cli;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DispatcherIntrospection))]
[JsonSerializable(typeof(AutomationScript))]
[JsonSerializable(typeof(AutomationStep))]
[JsonSerializable(typeof(MeshPeersResponse))]
[JsonSerializable(typeof(MeshGossipMemberSnapshot))]
[JsonSerializable(typeof(MeshGossipMemberMetadata))]
[JsonSerializable(typeof(NodeDrainSnapshot))]
[JsonSerializable(typeof(NodeDrainParticipantSnapshot))]
[JsonSerializable(typeof(NodeDrainCommandDto))]
internal sealed partial class OmniRelayCliJsonContext : JsonSerializerContext;
