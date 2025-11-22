using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Shards.ControlPlane;

namespace OmniRelay.Core.Diagnostics;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NodeDrainSnapshot))]
[JsonSerializable(typeof(NodeDrainParticipantSnapshot))]
[JsonSerializable(typeof(ShardListResponse))]
[JsonSerializable(typeof(ShardSummary))]
[JsonSerializable(typeof(ShardDiffResponse))]
[JsonSerializable(typeof(ShardDiffEntry))]
[JsonSerializable(typeof(ShardSimulationResponse))]
[JsonSerializable(typeof(ShardSimulationRequest))]
[JsonSerializable(typeof(ShardSimulationNode))]
[JsonSerializable(typeof(ShardSimulationAssignment))]
[JsonSerializable(typeof(ShardSimulationChange))]
[JsonSerializable(typeof(NodeDrainCommand))]
public sealed partial class ShardDiagnosticsJsonContext : JsonSerializerContext;
