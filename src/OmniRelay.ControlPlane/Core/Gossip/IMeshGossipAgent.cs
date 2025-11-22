using OmniRelay.Core.Transport;

namespace OmniRelay.Core.Gossip;

/// <summary>Represents a gossip agent lifecycle + diagnostic surface.</summary>
public interface IMeshGossipAgent : ILifecycle, IMeshMembershipSnapshotProvider
{
    /// <summary>Gets the advertised metadata for this node.</summary>
    MeshGossipMemberMetadata LocalMetadata { get; }

    /// <summary>Indicates whether the agent is enabled.</summary>
    bool IsEnabled { get; }
}
