using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Peers;

public sealed class TwoRandomPeerChooser : IPeerChooser
{
    private readonly ImmutableArray<IPeer> _peers;
    private readonly Random _random;

    public TwoRandomPeerChooser(params IPeer[] peers)
        : this(peers is null ? throw new ArgumentNullException(nameof(peers)) : ImmutableArray.Create(peers))
    {
    }

    public TwoRandomPeerChooser(ImmutableArray<IPeer> peers, Random? random = null)
    {
        if (peers.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one peer must be provided.", nameof(peers));
        }

        _peers = peers;
        _random = random ?? new Random();
    }

    public ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_peers.Length == 1)
        {
            return TryAcquire(meta, _peers[0], cancellationToken);
        }

        IPeer first = _peers[_random.Next(_peers.Length)];
        IPeer second;

        do
        {
            second = _peers[_random.Next(_peers.Length)];
        }
        while (ReferenceEquals(first, second) && _peers.Length > 1);

        var chosen = ChoosePreferred(first, second);
        return TryAcquire(meta, chosen, cancellationToken);
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

    private static ValueTask<Result<PeerLease>> TryAcquire(RequestMeta meta, IPeer peer, CancellationToken cancellationToken)
    {
        if (!peer.TryAcquire(cancellationToken))
        {
            PeerMetrics.RecordLeaseRejected(meta, peer.Identifier, "rejected");
            var exhausted = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.ResourceExhausted,
                "Selected peer rejected the lease.",
                transport: meta.Transport ?? "unknown");
            return ValueTask.FromResult(Err<PeerLease>(exhausted));
        }

        return ValueTask.FromResult(Ok(new PeerLease(peer, meta)));
    }
}
