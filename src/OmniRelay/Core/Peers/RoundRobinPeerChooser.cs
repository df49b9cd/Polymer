using System.Collections.Immutable;
using Hugo;
namespace OmniRelay.Core.Peers;

/// <summary>
/// Chooses peers in a round-robin fashion, skipping busy peers.
/// </summary>
public sealed class RoundRobinPeerChooser : IPeerChooser
{
    private readonly PeerListCoordinator _coordinator;
    private long _next = -1;

    public RoundRobinPeerChooser(params IPeer[] peers)
        : this(peers is null ? throw new ArgumentNullException(nameof(peers)) : peers.AsEnumerable(), leaseHealthProvider: null)
    {
    }

    public RoundRobinPeerChooser(ImmutableArray<IPeer> peers)
        : this(peers.AsEnumerable(), leaseHealthProvider: null)
    {
    }

    public RoundRobinPeerChooser(IEnumerable<IPeer> peers, PeerLeaseHealthTracker? leaseHealthTracker = null)
        : this(peers, (IPeerHealthSnapshotProvider?)leaseHealthTracker)
    {
    }

    public RoundRobinPeerChooser(IEnumerable<IPeer> peers, IPeerHealthSnapshotProvider? leaseHealthProvider)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var snapshot = peers.ToList();
        _coordinator = new PeerListCoordinator(snapshot, leaseHealthProvider);
    }

    public void UpdatePeers(IEnumerable<IPeer> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        _coordinator.UpdatePeers(peers);
    }

    public ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default)
        => _coordinator.AcquireAsync(meta, cancellationToken, SelectPeer);

    public void Dispose()
    {
        _coordinator.Dispose();
    }

    private IPeer? SelectPeer(IReadOnlyList<IPeer> peers)
    {
        if (peers.Count == 0)
        {
            return null;
        }

        var index = Interlocked.Increment(ref _next);
        var length = peers.Count;
        var slot = (int)(index % length);
        if (slot < 0)
        {
            slot += length;
        }

        return peers[slot];
    }
}
