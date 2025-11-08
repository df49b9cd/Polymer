using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Chooses peers in a round-robin fashion, skipping busy peers.
/// </summary>
public sealed class RoundRobinPeerChooser : IPeerChooser, IPeerSubscriber
{
    private readonly ImmutableArray<IPeer> _peers;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly PeerAvailabilitySignal? _availabilitySignal;
    private bool _disposed;
    private long _next = -1;

    public RoundRobinPeerChooser(params IPeer[] peers)
    {
        ArgumentNullException.ThrowIfNull(peers);

        _peers = [.. peers];
        _availabilitySignal = InitializeSubscriptions();
    }

    public RoundRobinPeerChooser(ImmutableArray<IPeer> peers)
    {
        _peers = peers;
        _availabilitySignal = InitializeSubscriptions();
    }

    public async ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_peers.IsDefaultOrEmpty)
        {
            var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "No peers are registered for the requested service.", transport: meta.Transport ?? "unknown");
            return Err<PeerLease>(error);
        }

        var waitDeadline = PeerChooserHelpers.ResolveDeadline(meta);

        while (true)
        {
            var length = _peers.Length;
            for (var attempt = 0; attempt < length; attempt++)
            {
                var index = Interlocked.Increment(ref _next);
                var resolved = _peers[(int)(index % length)];

                if (resolved.TryAcquire(cancellationToken))
                {
                    return Ok(new PeerLease(resolved, meta));
                }

                PeerMetrics.RecordLeaseRejected(meta, resolved.Identifier, "busy");
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
        var exhausted = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.ResourceExhausted, "All peers are busy.", transport: meta.Transport ?? "unknown");
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
