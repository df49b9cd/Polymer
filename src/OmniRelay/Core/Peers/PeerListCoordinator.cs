using System;
using System.Collections.Generic;
using System.Linq;
using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Central coordinator that tracks peer availability, subscriptions, and selection.
/// </summary>
internal sealed class PeerListCoordinator : IPeerSubscriber, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PeerRegistration> _registrations = new(StringComparer.Ordinal);
    private readonly List<IPeer> _availablePeers = new();
    private readonly PeerAvailabilitySignal _availabilitySignal = new();
    private bool _disposed;

    public PeerListCoordinator(IEnumerable<IPeer> peers)
    {
        UpdatePeers(peers);
    }

    public void UpdatePeers(IEnumerable<IPeer> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);

        lock (_lock)
        {
            var desired = new Dictionary<string, IPeer>(StringComparer.Ordinal);
            foreach (var peer in peers)
            {
                if (peer is null)
                {
                    continue;
                }

                desired[peer.Identifier] = peer;
            }

            foreach (var existing in _registrations.Keys.Except(desired.Keys).ToList())
            {
                RemovePeerInternal(existing);
            }

            foreach (var peer in desired.Values)
            {
                if (_registrations.TryGetValue(peer.Identifier, out var registration))
                {
                    if (!ReferenceEquals(registration.Peer, peer))
                    {
                        RemovePeerInternal(peer.Identifier);
                        AddPeerInternal(peer);
                    }
                }
                else
                {
                    AddPeerInternal(peer);
                }
            }

            if (_availablePeers.Count > 0)
            {
                _availabilitySignal.Signal();
            }
        }
    }

    public async ValueTask<Result<PeerLease>> AcquireAsync(
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<IPeer>, IPeer?> selector)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(selector);

        cancellationToken.ThrowIfCancellationRequested();

        if (!HasPeers())
        {
            var unavailable = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Unavailable,
                "No peers are registered for the requested service.",
                transport: meta.Transport ?? "unknown");
            return Err<PeerLease>(unavailable);
        }

        var waitDeadline = PeerChooserHelpers.ResolveDeadline(meta);

        while (true)
        {
            IPeer? candidate = null;

            lock (_lock)
            {
                if (_availablePeers.Count > 0)
                {
                    candidate = selector(_availablePeers);
                }
            }

            if (candidate is not null)
            {
                if (candidate.TryAcquire(cancellationToken))
                {
                    return Ok(new PeerLease(candidate, meta));
                }

                PeerMetrics.RecordLeaseRejected(meta, candidate.Identifier, "busy");
            }

            if (!HasPeers())
            {
                var unavailable = OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.Unavailable,
                    "No peers are registered for the requested service.",
                    transport: meta.Transport ?? "unknown");
                return Err<PeerLease>(unavailable);
            }

            if (!PeerChooserHelpers.TryGetWaitDelay(waitDeadline, out var delay))
            {
                break;
            }

            await _availabilitySignal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }

        PeerMetrics.RecordPoolExhausted(meta);
        var exhausted = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.ResourceExhausted,
            "All peers are busy.",
            transport: meta.Transport ?? "unknown");
        return Err<PeerLease>(exhausted);
    }

    public void NotifyStatusChanged(IPeer peer)
    {
        if (peer is null)
        {
            return;
        }

        lock (_lock)
        {
            if (!_registrations.TryGetValue(peer.Identifier, out var registration))
            {
                return;
            }

            var isAvailable = peer.Status.State == PeerState.Available;
            if (isAvailable == registration.IsAvailable)
            {
                return;
            }

            registration.IsAvailable = isAvailable;
            if (isAvailable)
            {
                _availablePeers.Add(peer);
                _availabilitySignal.Signal();
            }
            else
            {
                _availablePeers.Remove(peer);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_lock)
        {
            foreach (var registration in _registrations.Values)
            {
                registration.Subscription?.Dispose();
            }

            _registrations.Clear();
            _availablePeers.Clear();
        }

        _availabilitySignal.Dispose();
    }

    private bool HasPeers()
    {
        lock (_lock)
        {
            return _registrations.Count > 0;
        }
    }

    private void AddPeerInternal(IPeer peer)
    {
        var registration = new PeerRegistration(peer);
        if (peer is IPeerObservable observable)
        {
            registration.Subscription = observable.Subscribe(this);
        }

        registration.IsAvailable = peer.Status.State == PeerState.Available;
        _registrations.Add(peer.Identifier, registration);

        if (registration.IsAvailable)
        {
            _availablePeers.Add(peer);
            _availabilitySignal.Signal();
        }
    }

    private void RemovePeerInternal(string identifier)
    {
        if (!_registrations.Remove(identifier, out var registration))
        {
            return;
        }

        registration.Subscription?.Dispose();
        if (registration.IsAvailable)
        {
            _availablePeers.Remove(registration.Peer);
        }
    }

    private sealed class PeerRegistration
    {
        public PeerRegistration(IPeer peer)
        {
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
        }

        public IPeer Peer { get; }
        public IDisposable? Subscription { get; set; }
        public bool IsAvailable { get; set; }
    }
}
