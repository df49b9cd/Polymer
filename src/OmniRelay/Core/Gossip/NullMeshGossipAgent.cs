namespace OmniRelay.Core.Gossip;

/// <summary>No-op gossip agent used when gossip is disabled.</summary>
internal sealed class NullMeshGossipAgent : IMeshGossipAgent
{
    public static NullMeshGossipAgent Instance { get; } = new();

    public bool IsEnabled => false;

    public MeshGossipMemberMetadata LocalMetadata { get; } = new() { NodeId = string.Empty };

    public MeshGossipClusterView Snapshot() =>
        new(DateTimeOffset.UtcNow, [], string.Empty, MeshGossipOptions.CurrentSchemaVersion);

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
