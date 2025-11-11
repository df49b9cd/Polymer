using System.Collections.Immutable;
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
    private readonly List<IPeer> _availablePeers = [];
    private readonly PeerAvailabilitySignal _availabilitySignal;
    private readonly TimeProvider _timeProvider;
    private readonly IPeerHealthSnapshotProvider? _leaseHealthProvider;
    private bool _disposed;

    public PeerListCoordinator(IEnumerable<IPeer> peers)
        : this(peers, leaseHealthProvider: null, TimeProvider.System)
    {
    }

    public PeerListCoordinator(IEnumerable<IPeer> peers, PeerLeaseHealthTracker? leaseHealthTracker)
        : this(peers, leaseHealthTracker, TimeProvider.System)
    {
    }

    public PeerListCoordinator(IEnumerable<IPeer> peers, PeerLeaseHealthTracker? leaseHealthTracker, TimeProvider? timeProvider)
        : this(peers, (IPeerHealthSnapshotProvider?)leaseHealthTracker, timeProvider)
    {
    }

    public PeerListCoordinator(IEnumerable<IPeer> peers, IPeerHealthSnapshotProvider? leaseHealthProvider)
        : this(peers, leaseHealthProvider, TimeProvider.System)
    {
    }

    public PeerListCoordinator(IEnumerable<IPeer> peers, IPeerHealthSnapshotProvider? leaseHealthProvider, TimeProvider? timeProvider)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseHealthProvider = leaseHealthProvider;
        _availabilitySignal = new PeerAvailabilitySignal(_timeProvider);
        UpdatePeers(peers);
    }

    public ImmutableArray<PeerLeaseHealthSnapshot> LeaseHealth =>
        _leaseHealthProvider?.Snapshot() ?? ImmutableArray<PeerLeaseHealthSnapshot>.Empty;

    public void UpdatePeers(IEnumerable<IPeer> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);

        var desired = new Dictionary<string, IPeer>(StringComparer.Ordinal);
        foreach (var peer in peers)
        {
            if (peer is null)
            {
                continue;
            }

            desired[peer.Identifier] = peer;
        }

        lock (_lock)
        {
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
            cancellationToken.ThrowIfCancellationRequested();

            if (!HasPeers())
            {
                var unavailable = OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.Unavailable,
                    "No peers are registered for the requested service.",
                    transport: meta.Transport ?? "unknown");
                return Err<PeerLease>(unavailable);
            }

            var attemptResult = TryAcquireFromAvailablePeers(meta, selector, cancellationToken, out var lease);
            if (attemptResult == AcquisitionAttemptResult.Success && lease is not null)
            {
                return Ok(lease);
            }

            if (attemptResult == AcquisitionAttemptResult.Busy)
            {
                PeerMetrics.RecordPoolExhausted(meta);
                var exhausted = OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.ResourceExhausted,
                    "All peers are busy.",
                    transport: meta.Transport ?? "unknown");
                return Err<PeerLease>(exhausted);
            }

            if (attemptResult == AcquisitionAttemptResult.NoAvailablePeers)
            {
                if (waitDeadline is not null && PeerChooserHelpers.HasDeadlineElapsed(waitDeadline))
                {
                    var deadlineError = OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.DeadlineExceeded,
                        "Peer acquisition deadline expired.",
                        transport: meta.Transport ?? "unknown");
                    return Err<PeerLease>(deadlineError);
                }

                PeerMetrics.RecordPoolExhausted(meta);
                var unavailable = OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.ResourceExhausted,
                    "No peers are currently available.",
                    transport: meta.Transport ?? "unknown");
                return Err<PeerLease>(unavailable);
            }

            if (PeerChooserHelpers.HasDeadlineElapsed(waitDeadline))
            {
                var deadlineError = OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.DeadlineExceeded,
                    "Peer acquisition deadline expired.",
                    transport: meta.Transport ?? "unknown");
                return Err<PeerLease>(deadlineError);
            }

            var delay = PeerChooserHelpers.GetWaitDelay(waitDeadline);
            var waitResult = await Go.WithTimeoutAsync(
                async token =>
                {
                    await _availabilitySignal.WaitAsync(delay, token).ConfigureAwait(false);
                    return Ok(Unit.Value);
                },
                delay,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);

            if (waitResult.IsFailure && waitResult.Error?.Code == ErrorCodes.Canceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
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

    private AcquisitionAttemptResult TryAcquireFromAvailablePeers(
        RequestMeta meta,
        Func<IReadOnlyList<IPeer>, IPeer?> selector,
        CancellationToken cancellationToken,
        out PeerLease? lease)
    {
        lease = null;

        List<IPeer>? snapshot = null;
        lock (_lock)
        {
            if (_availablePeers.Count > 0)
            {
                snapshot = [.. _availablePeers];
            }
        }

        if (snapshot is null || snapshot.Count == 0)
        {
            return AcquisitionAttemptResult.NoAvailablePeers;
        }

        var candidates = snapshot;
        var attempted = false;

        while (candidates.Count > 0)
        {
            var candidate = selector(candidates);
            if (candidate is null)
            {
                break;
            }

            if (!RemoveCandidate(candidates, candidate))
            {
                continue;
            }

            attempted = true;

            if (_leaseHealthProvider is not null && !_leaseHealthProvider.IsPeerEligible(candidate.Identifier))
            {
                PeerMetrics.RecordLeaseRejected(meta, candidate.Identifier, "lease_unhealthy");
                continue;
            }

            if (candidate.TryAcquire(cancellationToken))
            {
                lease = new PeerLease(candidate, meta);
                return AcquisitionAttemptResult.Success;
            }

            PeerMetrics.RecordLeaseRejected(meta, candidate.Identifier, "busy");
        }

        return attempted ? AcquisitionAttemptResult.Busy : AcquisitionAttemptResult.NoAvailablePeers;
    }

    private static bool RemoveCandidate(IList<IPeer> peers, IPeer candidate)
    {
        for (var i = 0; i < peers.Count; i++)
        {
            if (ReferenceEquals(peers[i], candidate))
            {
                peers.RemoveAt(i);
                return true;
            }
        }

        return false;
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

    private enum AcquisitionAttemptResult
    {
        NoAvailablePeers,
        Busy,
        Success
    }
}
