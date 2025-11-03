using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Peers;

public sealed class FewestPendingPeerChooser : IPeerChooser
{
    private readonly ImmutableArray<IPeer> _peers;
    private readonly Random _random = new();

    public FewestPendingPeerChooser(params IPeer[] peers)
    {
        ArgumentNullException.ThrowIfNull(peers);

        if (peers.Length == 0)
        {
            throw new ArgumentException("At least one peer must be provided.", nameof(peers));
        }

        _peers = ImmutableArray.Create(peers);
    }

    public FewestPendingPeerChooser(ImmutableArray<IPeer> peers)
    {
        if (peers.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one peer must be provided.", nameof(peers));
        }

        _peers = peers;
    }

    public ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bestPeers = new List<IPeer>();
        var bestInflight = int.MaxValue;

        foreach (var peer in _peers)
        {
            var status = peer.Status;
            if (status.State != PeerState.Available)
            {
                continue;
            }

            if (status.Inflight < bestInflight)
            {
                bestInflight = status.Inflight;
                bestPeers.Clear();
                bestPeers.Add(peer);
            }
            else if (status.Inflight == bestInflight)
            {
                bestPeers.Add(peer);
            }
        }

        if (bestPeers.Count == 0)
        {
            PeerMetrics.RecordPoolExhausted(meta);
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.ResourceExhausted,
                "All peers are busy.",
                transport: meta.Transport ?? "unknown");
            return ValueTask.FromResult(Err<PeerLease>(error));
        }

        var chosen = bestPeers.Count == 1
            ? bestPeers[0]
            : bestPeers[_random.Next(bestPeers.Count)];

        if (!chosen.TryAcquire(cancellationToken))
        {
            PeerMetrics.RecordLeaseRejected(meta, chosen.Identifier, "rejected");
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.ResourceExhausted,
                "Selected peer rejected the lease.",
                transport: meta.Transport ?? "unknown");
            return ValueTask.FromResult(Err<PeerLease>(error));
        }

        return ValueTask.FromResult(Ok(new PeerLease(chosen, meta)));
    }
}
