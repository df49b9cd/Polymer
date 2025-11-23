using System.Collections.Immutable;
using OmniRelay.Core.Gossip;

namespace OmniRelay.IntegrationTests.Support;

internal sealed class FakeMeshGossipAgent : IMeshGossipAgent
{
    private readonly MeshGossipMemberMetadata _metadata;
    private readonly ImmutableArray<string> _peerNodeIds;
    private readonly string _endpoint;
    private readonly string _rack;
    private bool _running;

    public FakeMeshGossipAgent(string nodeId, IEnumerable<string>? peerNodeIds = null, string? endpoint = null, string? rack = null)
    {
        _endpoint = endpoint ?? "127.0.0.1:0";
        _rack = rack ?? "rack";
        _metadata = new MeshGossipMemberMetadata
        {
            NodeId = nodeId,
            Role = "dispatcher",
            ClusterId = "integration-cluster",
            Region = "integration-region",
            MeshVersion = "integration-test",
            Http3Support = true,
            Endpoint = _endpoint,
            Labels = new Dictionary<string, string>
            {
                ["rack"] = _rack
            }
        };
        _peerNodeIds = peerNodeIds?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
    }

    public MeshGossipMemberMetadata LocalMetadata => _metadata;

    public bool IsEnabled => true;

    public MeshGossipClusterView Snapshot()
    {
        var membersBuilder = ImmutableArray.CreateBuilder<MeshGossipMemberSnapshot>();

        membersBuilder.Add(new MeshGossipMemberSnapshot
        {
            NodeId = _metadata.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            Metadata = _metadata,
            LastSeen = DateTimeOffset.UtcNow,
            RoundTripTimeMs = 0.1
        });

        foreach (var peerId in _peerNodeIds)
        {
            membersBuilder.Add(new MeshGossipMemberSnapshot
            {
                NodeId = peerId,
                Status = MeshGossipMemberStatus.Alive,
                Metadata = new MeshGossipMemberMetadata
                {
                    NodeId = peerId,
                    Role = _metadata.Role,
                    ClusterId = _metadata.ClusterId,
                    Region = _metadata.Region,
                    MeshVersion = _metadata.MeshVersion,
                    Http3Support = true,
                    Endpoint = _endpoint,
                    Labels = new Dictionary<string, string>
                    {
                        ["rack"] = _rack
                    }
                },
                LastSeen = DateTimeOffset.UtcNow,
                RoundTripTimeMs = 5.0
            });
        }

        var members = membersBuilder.ToImmutable();
        return new MeshGossipClusterView(DateTimeOffset.UtcNow, members, _metadata.NodeId, MeshGossipOptions.CurrentSchemaVersion);
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
