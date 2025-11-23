using System.Collections.Immutable;
using Hugo;
using OmniRelay.Diagnostics;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Chooses two random peers and selects the one with fewer in-flight requests.
/// </summary>
public sealed class TwoRandomPeerChooser : IPeerChooser
{
    private readonly PeerListCoordinator _coordinator;
    private readonly Random _random;

    public static Result<TwoRandomPeerChooser> TryCreate(IEnumerable<IPeer> peers, Random? random = null, IPeerHealthSnapshotProvider? leaseHealthProvider = null)
    {
        if (peers is null)
        {
            return Result.Fail<TwoRandomPeerChooser>(
                Error.From("Peers collection is required.", "peers.argument_missing")
                    .WithMetadata("argument", nameof(peers)));
        }

        var snapshot = peers.ToList();
        if (snapshot.Count == 0)
        {
            return Result.Fail<TwoRandomPeerChooser>(
                Error.From("At least one peer must be provided.", "peers.none_provided"));
        }

        return Result.Ok(new TwoRandomPeerChooser(snapshot, random, leaseHealthProvider));
    }

    public TwoRandomPeerChooser(params IPeer[] peers)
        : this(peers is null ? throw new ArgumentNullException(nameof(peers)) : peers.AsEnumerable(), random: null, leaseHealthTracker: null)
    {
    }

    public TwoRandomPeerChooser(ImmutableArray<IPeer> peers, Random? random = null, PeerLeaseHealthTracker? leaseHealthTracker = null)
        : this(peers.AsEnumerable(), random, leaseHealthTracker)
    {
    }

    public TwoRandomPeerChooser(IEnumerable<IPeer> peers, Random? random = null, PeerLeaseHealthTracker? leaseHealthTracker = null)
        : this(peers, random, (IPeerHealthSnapshotProvider?)leaseHealthTracker)
    {
    }

    public TwoRandomPeerChooser(IEnumerable<IPeer> peers, IPeerHealthSnapshotProvider? leaseHealthProvider)
        : this(peers, random: null, leaseHealthProvider)
    {
    }

    public TwoRandomPeerChooser(IEnumerable<IPeer> peers, Random? random, IPeerHealthSnapshotProvider? leaseHealthProvider)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var snapshot = peers.ToList();
        if (snapshot.Count == 0)
        {
            throw new ArgumentException("At least one peer must be provided.", nameof(peers));
        }

        _coordinator = new PeerListCoordinator(snapshot, leaseHealthProvider);
        _random = random ?? Random.Shared;
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
        var count = peers.Count;
        if (count == 0)
        {
            return null;
        }

        if (count == 1)
        {
            return peers[0];
        }

        var firstIndex = _random.Next(count);
        var secondIndex = firstIndex;
        while (secondIndex == firstIndex)
        {
            secondIndex = _random.Next(count);
        }

        var first = peers[firstIndex];
        var second = peers[secondIndex];
        return ChoosePreferred(first, second);
    }

    private static IPeer ChoosePreferred(IPeer first, IPeer second)
    {
        var status1 = first.Status;
        var status2 = second.Status;

        if (status1.State != PeerState.Available)
        {
            return status2.State == PeerState.Available ? second : first;
        }

        if (status2.State != PeerState.Available)
        {
            return first;
        }

        if (status1.Inflight < status2.Inflight)
        {
            return first;
        }

        if (status2.Inflight < status1.Inflight)
        {
            return second;
        }

        return first;
    }
}
