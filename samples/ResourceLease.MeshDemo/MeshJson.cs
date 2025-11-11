using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.Core.Peers;
using OmniRelay.Dispatcher;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal static class MeshJson
{
    public static MeshJsonContext Context { get; } = new MeshJsonContext(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PeerLeaseHealthDiagnostics))]
[JsonSerializable(typeof(PeerLeaseHealthSummary))]
[JsonSerializable(typeof(ResourceLeaseBackpressureSignal))]
[JsonSerializable(typeof(MeshEnqueueRequest))]
[JsonSerializable(typeof(LakehouseCatalogOperation))]
[JsonSerializable(typeof(LakehouseCatalogSnapshot))]
[JsonSerializable(typeof(LakehouseCatalogTableState))]
internal sealed partial class MeshJsonContext : JsonSerializerContext;
