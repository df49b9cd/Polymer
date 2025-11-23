using System.Collections.Immutable;
using Hugo;
using OmniRelay.Diagnostics;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Chooses the peer with the fewest in-flight requests, breaking ties randomly.
/// </summary>
public sealed class FewestPendingPeerChooser : IPeerChooser
{
    private readonly PeerListCoordinator _coordinator;

    public static Result<FewestPendingPeerChooser> TryCreate(IEnumerable<IPeer> peers, Random? random = null, IPeerHealthSnapshotProvider? leaseHealthProvider = null)
    {
        if (peers is null)
        {
            return Result.Fail<FewestPendingPeerChooser>(
                Error.From("Peers collection is required.", "peers.argument_missing")
                    .WithMetadata("argument", nameof(peers)));
        }

        var snapshot = peers.ToList();
        if (snapshot.Count == 0)
        {
            return Result.Fail<FewestPendingPeerChooser>(
                Error.From("At least one peer must be provided.", "peers.none_provided"));
        }

        return Result.Ok(new FewestPendingPeerChooser(snapshot, random, leaseHealthProvider));
    }

    public FewestPendingPeerChooser(params IPeer[] peers)
        : this(peers is null ? throw new ArgumentNullException(nameof(peers)) : peers.AsEnumerable(), random: null, leaseHealthTracker: null)
    {
    }

    public FewestPendingPeerChooser(ImmutableArray<IPeer> peers, Random? random = null, PeerLeaseHealthTracker? leaseHealthTracker = null)
        : this(peers.AsEnumerable(), random, leaseHealthTracker)
    {
    }

    public FewestPendingPeerChooser(IEnumerable<IPeer> peers, Random? random = null, PeerLeaseHealthTracker? leaseHealthTracker = null)
        : this(peers, random, (IPeerHealthSnapshotProvider?)leaseHealthTracker)
    {
    }

    public FewestPendingPeerChooser(IEnumerable<IPeer> peers, IPeerHealthSnapshotProvider? leaseHealthProvider)
        : this(peers, random: null, leaseHealthProvider)
    {
    }

    public FewestPendingPeerChooser(IEnumerable<IPeer> peers, Random? random, IPeerHealthSnapshotProvider? leaseHealthProvider)
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
        if (peers.Count == 0)
        {
            return null;
        }

        IPeer? chosen = null;
        var bestInflight = int.MaxValue;
        var tieCount = 0;

        foreach (var peer in peers)
        {
            var status = peer.Status;
            var inflight = status.Inflight;

            if (inflight < bestInflight)
            {
                bestInflight = inflight;
                chosen = peer;
                tieCount = 1;
            }
            else if (inflight == bestInflight)
            {
                tieCount++;
                if (_random.Next(tieCount) == 0)
                {
                    chosen = peer;
                }
            }
        }

        return chosen;
    }

    private readonly Random _random;
}
