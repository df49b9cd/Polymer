using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using OmniRelay.Diagnostics.Alerting;

namespace OmniRelay.Diagnostics;

/// <summary>
/// Tracks SafeTaskQueue lease heartbeats and membership gossip for metadata peers.
/// </summary>
public sealed class PeerLeaseHealthTracker : IPeerHealthSnapshotProvider
{
    private readonly ConcurrentDictionary<string, PeerLeaseHealthState> _states = new(StringComparer.Ordinal);
    private readonly TimeSpan _heartbeatGracePeriod;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertPublisher? _alertPublisher;

    public PeerLeaseHealthTracker(TimeSpan? heartbeatGracePeriod = null, TimeProvider? timeProvider = null, IAlertPublisher? alertPublisher = null)
    {
        _heartbeatGracePeriod = heartbeatGracePeriod ?? TimeSpan.FromSeconds(30);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _alertPublisher = alertPublisher;
        PeerLeaseHealthMetrics.RegisterTracker(this);
    }

    /// <summary>Records a new lease assignment for the specified peer.</summary>
    public void RecordLeaseAssignment(string peerId, PeerLeaseHandle handle, string? resourceType = null, string? resourceId = null)
    {
        var state = GetOrCreateState(peerId);
        var now = _timeProvider.GetUtcNow();
        state.RecordAssignment(handle, now, resourceType, resourceId);
        PeerLeaseHealthSignals.RecordLeaseAssignment(peerId, resourceType, resourceId);
    }

    /// <summary>Marks the lease as released and records whether it was requeued.</summary>
    public void RecordLeaseReleased(string peerId, PeerLeaseHandle handle, bool requeued)
    {
        var state = GetOrCreateState(peerId);
        var now = _timeProvider.GetUtcNow();
        state.RecordRelease(handle, now, requeued);
    }

    /// <summary>Records a heartbeat from the peer for the associated lease.</summary>
    public void RecordLeaseHeartbeat(string peerId, PeerLeaseHandle? handle = null, long? pendingCount = null)
    {
        var state = GetOrCreateState(peerId);
        var now = _timeProvider.GetUtcNow();
        state.RecordHeartbeat(now, handle, pendingCount);
        PeerLeaseHealthSignals.RecordLeaseHeartbeat(peerId);
    }

    /// <summary>Registers a disconnect or failure signal for the peer.</summary>
    public void RecordDisconnect(string peerId, string? reason = null)
    {
        var state = GetOrCreateState(peerId);
        var now = _timeProvider.GetUtcNow();
        state.RecordDisconnect(now, reason);
        PeerLeaseHealthSignals.RecordLeaseDisconnect(peerId, reason);
        PublishAlert(peerId, reason);
    }

    /// <summary>Stores metadata learned through gossip (for example region/zone, namespace ownership).</summary>
    public void RecordGossip(string peerId, IReadOnlyDictionary<string, string> metadata)
    {
        var state = GetOrCreateState(peerId);
        state.RecordGossip(metadata);
    }

    /// <summary>Determines whether the peer is eligible for new lease assignments.</summary>
    public bool IsPeerEligible(string peerId)
    {
        if (!_states.TryGetValue(peerId, out var state))
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        return state.IsHealthy(now, _heartbeatGracePeriod);
    }

    /// <summary>Returns an immutable snapshot for all tracked peers.</summary>
    public ImmutableArray<PeerLeaseHealthSnapshot> Snapshot()
    {
        var builder = ImmutableArray.CreateBuilder<PeerLeaseHealthSnapshot>(_states.Count);
        ComputeSummary(builder);
        return builder.ToImmutable();
    }

    internal PeerLeaseHealthSummary GetSummary() =>
        ComputeSummary(builder: null);

    private PeerLeaseHealthSummary ComputeSummary(ImmutableArray<PeerLeaseHealthSnapshot>.Builder? builder)
    {
        var now = _timeProvider.GetUtcNow();
        var healthyCount = 0;
        var unhealthyCount = 0;
        var pendingReassignments = 0;

        foreach (var state in _states.Values)
        {
            var snapshot = state.ToSnapshot(now, _heartbeatGracePeriod);
            builder?.Add(snapshot);
            if (snapshot.IsHealthy)
            {
                healthyCount++;
            }
            else
            {
                unhealthyCount++;
            }

            pendingReassignments += snapshot.PendingReassignments;
        }

        return new PeerLeaseHealthSummary(healthyCount, unhealthyCount, pendingReassignments);
    }

    private PeerLeaseHealthState GetOrCreateState(string peerId) =>
        _states.GetOrAdd(peerId, static id => new PeerLeaseHealthState(id));

    private void PublishAlert(string peerId, string? reason)
    {
        if (_alertPublisher is null)
        {
            return;
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["peer.id"] = peerId,
            ["reason"] = reason ?? "unknown"
        };

        var alert = new AlertEvent(
            "peer_disconnect",
            "warning",
            $"Peer {peerId} disconnected.",
            metadata);

        var publishTask = _alertPublisher.PublishAsync(alert);
        if (!publishTask.IsCompletedSuccessfully)
        {
            _ = ObservePublishAsync(publishTask);
        }
    }

    private static async Task ObservePublishAsync(ValueTask publishTask)
    {
        try
        {
            await publishTask.ConfigureAwait(false);
        }
        catch
        {
            // Swallow background alert failures.
        }
    }

    private sealed class PeerLeaseHealthState(string peerId)
    {
        private readonly string _peerId = peerId;
        private readonly object _lock = new();
        private readonly Dictionary<Guid, PeerLeaseHandle> _activeLeases = [];
        private ImmutableDictionary<string, string> _metadata = [];
        private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
        private DateTimeOffset? _lastDisconnect;
        private bool _isDisconnected;
        private int _pendingReassignments;

        public void RecordAssignment(PeerLeaseHandle handle, DateTimeOffset observedAt, string? resourceType, string? resourceId)
        {
            lock (_lock)
            {
                _activeLeases[handle.LeaseId] = handle;
                _lastHeartbeat = observedAt;
                _isDisconnected = false;
                if (resourceType is not null)
                {
                    _metadata = _metadata.SetItem("resource.type", resourceType);
                }

                if (resourceId is not null)
                {
                    _metadata = _metadata.SetItem("resource.id", resourceId);
                }

                if (_pendingReassignments > 0)
                {
                    _pendingReassignments--;
                }
            }
        }

        public void RecordRelease(PeerLeaseHandle handle, DateTimeOffset observedAt, bool requeued)
        {
            lock (_lock)
            {
                _activeLeases.Remove(handle.LeaseId);
                _lastHeartbeat = observedAt;
                if (requeued)
                {
                    _pendingReassignments++;
                }
            }
        }

        public void RecordHeartbeat(DateTimeOffset observedAt, PeerLeaseHandle? handle, long? pendingCount)
        {
            lock (_lock)
            {
                _lastHeartbeat = observedAt;
                _isDisconnected = false;
                if (handle is PeerLeaseHandle lease && !_activeLeases.ContainsKey(lease.LeaseId))
                {
                    _activeLeases[lease.LeaseId] = lease;
                }

                if (pendingCount is not null)
                {
                    _metadata = _metadata.SetItem("pending.count", pendingCount.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        public void RecordDisconnect(DateTimeOffset observedAt, string? reason)
        {
            lock (_lock)
            {
                _lastDisconnect = observedAt;
                _isDisconnected = true;
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    _metadata = _metadata.SetItem("disconnect.reason", reason);
                }
            }
        }

        public void RecordGossip(IReadOnlyDictionary<string, string> metadata)
        {
            lock (_lock)
            {
                foreach (var kvp in metadata)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value is not null)
                    {
                        _metadata = _metadata.SetItem(kvp.Key, kvp.Value);
                    }
                }
            }
        }

        public bool IsHealthy(DateTimeOffset now, TimeSpan gracePeriod)
        {
            lock (_lock)
            {
                if (_isDisconnected)
                {
                    return false;
                }

                if (_lastHeartbeat == DateTimeOffset.MinValue)
                {
                    return true;
                }

                return now - _lastHeartbeat <= gracePeriod;
            }
        }

        public PeerLeaseHealthSnapshot ToSnapshot(DateTimeOffset now, TimeSpan gracePeriod)
        {
            lock (_lock)
            {
                var leases = _activeLeases.Values.ToImmutableArray();
                var metadata = _metadata;
                var healthy = !_isDisconnected && (_lastHeartbeat == DateTimeOffset.MinValue || now - _lastHeartbeat <= gracePeriod);
                return new PeerLeaseHealthSnapshot(
                    _peerId,
                    _lastHeartbeat,
                    _lastDisconnect,
                    healthy,
                    leases.Length,
                    _pendingReassignments,
                    leases,
                    metadata);
            }
        }
    }
}
