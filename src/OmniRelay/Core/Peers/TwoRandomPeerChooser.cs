using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Chooses two random peers and selects the one with fewer in-flight requests.
/// </summary>
public sealed class TwoRandomPeerChooser : IPeerChooser, IPeerSubscriber
{
    private readonly ImmutableArray<IPeer> _peers;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly PeerAvailabilitySignal? _availabilitySignal;
    private readonly Random _random;
    private bool _disposed;

    public TwoRandomPeerChooser(params IPeer[] peers)
        : this(peers is null ? throw new ArgumentNullException(nameof(peers)) : [.. peers], null)
    {
    }

    public TwoRandomPeerChooser(ImmutableArray<IPeer> peers, Random? random = null)
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
            if (TryAcquireOnce(meta, cancellationToken, out var lease))
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
        return CreateExhaustedResult(meta);
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

    private bool TryAcquireOnce(RequestMeta meta, CancellationToken cancellationToken, out PeerLease? lease)
    {
        if (_peers.Length == 1)
        {
            return TryAcquire(meta, _peers[0], cancellationToken, out lease);
        }

        IPeer first = _peers[_random.Next(_peers.Length)];
        IPeer second;

        do
        {
            second = _peers[_random.Next(_peers.Length)];
        }
        while (ReferenceEquals(first, second) && _peers.Length > 1);

        var chosen = ChoosePreferred(first, second);
        if (TryAcquire(meta, chosen, cancellationToken, out lease))
        {
            return true;
        }

        var alternate = ReferenceEquals(chosen, first) ? second : first;
        if (!ReferenceEquals(chosen, alternate) && TryAcquire(meta, alternate, cancellationToken, out lease))
        {
            return true;
        }

        lease = null;
        return false;
    }

    private static bool TryAcquire(RequestMeta meta, IPeer peer, CancellationToken cancellationToken, out PeerLease? lease)
    {
        if (!peer.TryAcquire(cancellationToken))
        {
            PeerMetrics.RecordLeaseRejected(meta, peer.Identifier, "busy");
            lease = null;
            return false;
        }

        lease = new PeerLease(peer, meta);
        return true;
    }

    private static Result<PeerLease> CreateExhaustedResult(RequestMeta meta)
    {
        var exhausted = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.ResourceExhausted,
            "All peers are busy.",
            transport: meta.Transport ?? "unknown");
        return Err<PeerLease>(exhausted);
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
