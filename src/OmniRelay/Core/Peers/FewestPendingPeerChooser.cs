using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Chooses the peer with the fewest in-flight requests, breaking ties randomly.
/// </summary>
public sealed class FewestPendingPeerChooser : IPeerChooser, IPeerSubscriber
{
    private readonly ImmutableArray<IPeer> _peers;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly PeerAvailabilitySignal? _availabilitySignal;
    private readonly Random _random;
    private bool _disposed;

    public FewestPendingPeerChooser(params IPeer[] peers)
        : this(peers is null ? throw new ArgumentNullException(nameof(peers)) : ImmutableArray.Create(peers))
    {
    }

    public FewestPendingPeerChooser(ImmutableArray<IPeer> peers, Random? random = null)
    {
        if (peers.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one peer must be provided.", nameof(peers));
        }

        _peers = peers;
        _random = random ?? Random.Shared;
        _availabilitySignal = InitializeSubscriptions();
    }

    public async ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var waitDeadline = PeerChooserHelpers.ResolveDeadline(meta);

        while (true)
        {
            if (TryAcquireFromSnapshot(meta, cancellationToken, out var lease))
            {
                return Ok(lease!);
            }

            if (!PeerChooserHelpers.TryGetWaitDelay(waitDeadline, out var delay))
            {
                break;
            }

            if (_availabilitySignal is { } signal)
            {
                await signal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        PeerMetrics.RecordPoolExhausted(meta);
        var exhausted = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.ResourceExhausted,
            "All peers are busy.",
            transport: meta.Transport ?? "unknown");
        return Err<PeerLease>(exhausted);
    }

    private bool TryAcquireFromSnapshot(RequestMeta meta, CancellationToken cancellationToken, out PeerLease? lease)
    {
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

        while (bestPeers.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var index = bestPeers.Count == 1
                ? 0
                : _random.Next(bestPeers.Count);
            var candidate = bestPeers[index];
            bestPeers.RemoveAt(index);

            if (candidate.TryAcquire(cancellationToken))
            {
                lease = new PeerLease(candidate, meta);
                return true;
            }

            PeerMetrics.RecordLeaseRejected(meta, candidate.Identifier, "busy");
        }

        lease = null;
        return false;
    }

    public void NotifyStatusChanged(IPeer peer)
    {
        _availabilitySignal?.Signal();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _availabilitySignal?.Dispose();
    }

    private PeerAvailabilitySignal? InitializeSubscriptions()
    {
        PeerAvailabilitySignal? signal = null;
        foreach (var peer in _peers)
        {
            if (peer is not IPeerObservable observable)
            {
                continue;
            }

            signal ??= new PeerAvailabilitySignal();
            _subscriptions.Add(observable.Subscribe(this));
        }

        return signal;
    }
}
