using OmniRelay.Core.Gossip;

namespace OmniRelay.Cli;

internal sealed class MeshPeersResponse
{
    public string SchemaVersion { get; set; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; set; }

    public string LocalNodeId { get; set; } = string.Empty;

    public MeshGossipMemberSnapshot[] Peers { get; set; } = [];
}
