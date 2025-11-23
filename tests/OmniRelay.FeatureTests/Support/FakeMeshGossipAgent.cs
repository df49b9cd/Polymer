using System.Collections.Immutable;
using OmniRelay.Core.Gossip;

namespace OmniRelay.FeatureTests.Support;

internal sealed class FakeMeshGossipAgent : IMeshGossipAgent
{
    private bool _running;

    public FakeMeshGossipAgent()
    {
        LocalMetadata = new MeshGossipMemberMetadata
        {
            NodeId = Guid.NewGuid().ToString("N"),
            Role = "feature-test",
            ClusterId = "feature-cluster",
            Region = "local",
            MeshVersion = "test",
            Http3Support = false
        };
    }

    public MeshGossipMemberMetadata LocalMetadata { get; }

    public bool IsEnabled => true;

    public MeshGossipClusterView Snapshot()
    {
        var member = new MeshGossipMemberSnapshot
        {
            NodeId = LocalMetadata.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            Metadata = LocalMetadata,
            LastSeen = DateTimeOffset.UtcNow
        };
        return new MeshGossipClusterView(DateTimeOffset.UtcNow, ImmutableArray.Create(member), LocalMetadata.NodeId, MeshGossipOptions.CurrentSchemaVersion);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _running = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        return ValueTask.CompletedTask;
    }

    public bool IsRunning => _running;
}
