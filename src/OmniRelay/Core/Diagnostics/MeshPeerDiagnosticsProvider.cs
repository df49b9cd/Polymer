using System.Collections.Generic;
using System.Linq;
using OmniRelay.Core.Gossip;
using OmniRelay.Diagnostics;

namespace OmniRelay.Core.Diagnostics;

/// <summary>Adapts <see cref="IMeshGossipAgent"/> snapshots to the shared peer diagnostics contracts.</summary>
internal sealed class MeshPeerDiagnosticsProvider(IMeshGossipAgent agent) : IPeerDiagnosticsProvider
{
    private readonly IMeshGossipAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public PeerDiagnosticsResponse CreateSnapshot()
    {
        var snapshot = _agent.Snapshot();
        var peers = snapshot.Members
            .Select(member => new PeerDiagnosticsPeer(
                member.NodeId,
                member.Status.ToString(),
                member.LastSeen,
                member.RoundTripTimeMs,
                new PeerDiagnosticsPeerMetadata(
                    member.Metadata.Role,
                    member.Metadata.ClusterId,
                    member.Metadata.Region,
                    member.Metadata.MeshVersion,
                    member.Metadata.Http3Support,
                    member.Metadata.Endpoint,
                    member.Metadata.MetadataVersion,
                    member.Metadata.Labels)))
            .ToArray();

        return new PeerDiagnosticsResponse(
            snapshot.SchemaVersion,
            snapshot.GeneratedAt,
            snapshot.LocalNodeId,
            peers);
    }
}
