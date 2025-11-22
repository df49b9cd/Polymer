using System.Collections.Immutable;
using Hugo;
using OmniRelay.Diagnostics;
using OmniRelay.Errors;
using static Hugo.Functional;
using static Hugo.Go;

namespace OmniRelay.Core.Peers;

#pragma warning disable CA1068 // CancellationToken parameter precedes delegate for OmniRelay peer coordination contract.

/// <summary>
/// Central coordinator that tracks peer availability, subscriptions, and selection.
/// </summary>
internal sealed class PeerListCoordinator : IPeerSubscriber, IDisposable
{
    private readonly Hugo.Mutex _stateMutex = new();
    private readonly Dictionary<string, PeerRegistration> _registrations = new(StringComparer.Ordinal);
    private readonly List<IPeer> _availablePeers = [];
    private IPeer[] _availableSnapshot = Array.Empty<IPeer>();
    private readonly PeerAvailabilitySignal _availabilitySignal;
    private readonly TimeProvider _timeProvider;
    private readonly IPeerHealthSnapshotProvider? _leaseHealthProvider;
    private int _peerCount;
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
        _leaseHealthProvider?.Snapshot() ?? [];

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

        using var scope = _stateMutex.EnterScope();

        foreach (var existing in _registrations.Keys.Except(desired.Keys).ToList())
        {
            RemovePeerInternal(existing);
        }

        foreach (var peer in desired.Values)
        {
            if (_registrations.TryGetValue(peer.Identifier, out var registration) && ReferenceEquals(registration.Peer, peer))
            {
                continue;
            }

            if (_registrations.ContainsKey(peer.Identifier))
            {
                RemovePeerInternal(peer.Identifier);
            }

            AddPeerInternal(peer);
        }

        _peerCount = _registrations.Count;
        RefreshAvailableSnapshotLocked();
    }

    public ValueTask<Result<PeerLease>> AcquireAsync(
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<IPeer>, IPeer?> selector)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(selector);

        cancellationToken.ThrowIfCancellationRequested();

        var context = new AcquireContext(meta, selector, PeerChooserHelpers.ResolveDeadline(meta));

        return Result
            .Ok(context)
            .Ensure(_ => HasPeers(), ctx => CreateUnavailable(ctx.Meta, "No peers are registered for the requested service."))
            .ThenValueTaskAsync(AcquireLoopAsync, cancellationToken);
    }

    private async ValueTask<Result<PeerLease>> AcquireLoopAsync(AcquireContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!HasPeers())
            {
                return Err<PeerLease>(CreateUnavailable(context.Meta, "No peers are registered for the requested service."));
            }

            var attemptResult = TryAcquireFromAvailablePeers(context.Meta, context.Selector, cancellationToken, out var lease);
            if (attemptResult == AcquisitionAttemptResult.Success && lease is not null)
            {
                return Ok(lease);
            }

            if (attemptResult == AcquisitionAttemptResult.Busy)
            {
                PeerMetrics.RecordPoolExhausted(context.Meta);
                return Err<PeerLease>(CreateBusyError(context.Meta));
            }

            if (attemptResult == AcquisitionAttemptResult.NoAvailablePeers && context.WaitDeadline is null)
            {
                PeerMetrics.RecordPoolExhausted(context.Meta);
                return Err<PeerLease>(CreateNoAvailabilityError(context.Meta));
            }

            if (context.WaitDeadline is { } waitDeadline && PeerChooserHelpers.HasDeadlineElapsed(waitDeadline))
            {
                PeerMetrics.RecordPoolExhausted(context.Meta);
                return Err<PeerLease>(CreateDeadlineError(context.Meta));
            }

            var waitOutcome = await WaitForAvailabilityAsync(context, cancellationToken).ConfigureAwait(false);
            if (waitOutcome.IsFailure)
            {
                if (waitOutcome.Error?.Code == ErrorCodes.Canceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (waitOutcome.Error?.Code == ErrorCodes.Timeout)
                {
                    if (context.WaitDeadline is { } deadline && PeerChooserHelpers.HasDeadlineElapsed(deadline))
                    {
                        PeerMetrics.RecordPoolExhausted(context.Meta);
                        return Err<PeerLease>(CreateDeadlineError(context.Meta));
                    }

                    continue;
                }

                return waitOutcome.CastFailure<PeerLease>();
            }
        }
    }

    private ValueTask<Result<Unit>> WaitForAvailabilityAsync(AcquireContext context, CancellationToken cancellationToken)
    {
        var delay = PeerChooserHelpers.GetWaitDelay(context.WaitDeadline);
        return _availabilitySignal.WaitAsync(delay, cancellationToken);
    }

    private static Error CreateUnavailable(RequestMeta meta, string message) =>
        OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.Unavailable,
            message,
            transport: meta.Transport ?? "unknown");

    private static Error CreateBusyError(RequestMeta meta) =>
        OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.ResourceExhausted,
            "All peers are busy.",
            transport: meta.Transport ?? "unknown");

    private static Error CreateNoAvailabilityError(RequestMeta meta) =>
        OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.ResourceExhausted,
            "No peers are currently available.",
            transport: meta.Transport ?? "unknown");

    private static Error CreateDeadlineError(RequestMeta meta) =>
        OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.DeadlineExceeded,
            "Peer acquisition deadline expired.",
            transport: meta.Transport ?? "unknown");

    public void NotifyStatusChanged(IPeer peer)
    {
        if (peer is null)
        {
            return;
        }

        using var scope = _stateMutex.EnterScope();

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

        RefreshAvailableSnapshotLocked();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        using var scope = _stateMutex.EnterScope();

        foreach (var registration in _registrations.Values)
        {
            registration.Subscription?.Dispose();
        }

        _registrations.Clear();
        _availablePeers.Clear();
        _peerCount = 0;
        RefreshAvailableSnapshotLocked();

        _availabilitySignal.Dispose();
    }

    private bool HasPeers() => Volatile.Read(ref _peerCount) > 0;

    private AcquisitionAttemptResult TryAcquireFromAvailablePeers(
        RequestMeta meta,
        Func<IReadOnlyList<IPeer>, IPeer?> selector,
        CancellationToken cancellationToken,
        out PeerLease? lease)
    {
        lease = null;

        var snapshot = Volatile.Read(ref _availableSnapshot);
        if (snapshot.Length == 0)
        {
            return AcquisitionAttemptResult.NoAvailablePeers;
        }

        var candidates = new List<IPeer>(snapshot);
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

    private static bool RemoveCandidate(List<IPeer> peers, IPeer candidate)
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

        RefreshAvailableSnapshotLocked();
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

        RefreshAvailableSnapshotLocked();
    }

    private void RefreshAvailableSnapshotLocked()
    {
        var snapshot = _availablePeers.Count == 0
            ? Array.Empty<IPeer>()
            : _availablePeers.ToArray();

        Volatile.Write(ref _availableSnapshot, snapshot);
    }

    private sealed record AcquireContext(
        RequestMeta Meta,
        Func<IReadOnlyList<IPeer>, IPeer?> Selector,
        DateTimeOffset? WaitDeadline);

    private sealed class PeerRegistration(IPeer peer)
    {
        public IPeer Peer { get; } = peer ?? throw new ArgumentNullException(nameof(peer));
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

#pragma warning restore CA1068
