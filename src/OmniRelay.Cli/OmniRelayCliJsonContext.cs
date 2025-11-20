using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Shards.ControlPlane;
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
[JsonSerializable(typeof(ShardListResponse))]
[JsonSerializable(typeof(ShardDiffResponse))]
[JsonSerializable(typeof(ShardDiffEntry))]
[JsonSerializable(typeof(ShardSimulationResponse))]
[JsonSerializable(typeof(ShardSummary))]
[JsonSerializable(typeof(ShardSimulationAssignment))]
[JsonSerializable(typeof(ShardSimulationChange))]
[JsonSerializable(typeof(ShardSimulationRequest))]
[JsonSerializable(typeof(MeshConfigValidationReport))]
[JsonSerializable(typeof(Modules.ProgramMeshModule.LeadershipSnapshotResponse))]
[JsonSerializable(typeof(Modules.ProgramMeshModule.LeadershipEventDto))]
internal sealed partial class OmniRelayCliJsonContext : JsonSerializerContext;
