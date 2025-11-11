using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace OmniRelay.Core.Gossip;

/// <summary>
/// Thread-safe membership table that tracks peer gossip state and surfaces snapshots.
/// </summary>
internal sealed class MeshGossipMembershipTable
{
    private readonly ConcurrentDictionary<string, MeshGossipMemberState> _members = new(StringComparer.Ordinal);
    private readonly string _localNodeId;
    private readonly TimeProvider _timeProvider;

    public MeshGossipMembershipTable(string localNodeId, MeshGossipMemberMetadata metadata, TimeProvider timeProvider)
    {
        _localNodeId = localNodeId ?? throw new ArgumentNullException(nameof(localNodeId));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var now = _timeProvider.GetUtcNow();
        var state = MeshGossipMemberState.CreateLocal(metadata, now);
        _members[localNodeId] = state;
    }

    /// <summary>Gets the metadata currently advertised for the local node.</summary>
    public MeshGossipMemberMetadata LocalMetadata =>
        _members.TryGetValue(_localNodeId, out var state) ? state.Metadata : new MeshGossipMemberMetadata { NodeId = _localNodeId };

    /// <summary>Updates the local metadata and bumps the metadata version.</summary>
    public MeshGossipMemberMetadata RefreshLocalMetadata(Func<MeshGossipMemberMetadata, MeshGossipMemberMetadata> updater)
    {
        while (true)
        {
            if (!_members.TryGetValue(_localNodeId, out var state))
            {
                throw new InvalidOperationException("Local node metadata not initialized.");
            }

            var current = state.Metadata;
            var updated = updater(current) ?? current;
            updated = new MeshGossipMemberMetadata
            {
                NodeId = updated.NodeId,
                Role = updated.Role,
                ClusterId = updated.ClusterId,
                Region = updated.Region,
                MeshVersion = updated.MeshVersion,
                Http3Support = updated.Http3Support,
                MetadataVersion = current.MetadataVersion + 1,
                Endpoint = updated.Endpoint,
                Labels = updated.Labels
            };

            var now = _timeProvider.GetUtcNow();
            var refreshed = state with
            {
                Metadata = updated,
                LastSeen = now,
                Status = MeshGossipMemberStatus.Alive
            };

            if (_members.TryUpdate(_localNodeId, refreshed, state))
            {
                return updated;
            }
        }
    }

    /// <summary>Marks the specified peer as observed.</summary>
    public void MarkObserved(MeshGossipMemberSnapshot snapshot, double? rttMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = _timeProvider.GetUtcNow();

        _members.AddOrUpdate(
            snapshot.NodeId,
            _ => MeshGossipMemberState.CreateFromSnapshot(snapshot, now, rttMilliseconds),
            (_, existing) => existing.Merge(snapshot, now, rttMilliseconds));
    }

    /// <summary>Marks the sender of an envelope as observed.</summary>
    public void MarkSender(MeshGossipEnvelope envelope, double? rttMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var now = _timeProvider.GetUtcNow();
        var snapshot = new MeshGossipMemberSnapshot
        {
            NodeId = envelope.Sender.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            Metadata = envelope.Sender,
            LastSeen = now
        };

        _members.AddOrUpdate(
            envelope.Sender.NodeId,
            _ => MeshGossipMemberState.CreateFromSnapshot(snapshot, now, rttMilliseconds),
            (_, existing) => existing.Merge(snapshot, now, rttMilliseconds));
    }

    /// <summary>Marks peers as suspect or left based on heartbeat timers.</summary>
    public void Sweep(TimeSpan suspicionInterval, TimeSpan leaveInterval)
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var entry in _members.Values)
        {
            if (entry.NodeId == _localNodeId)
            {
                continue;
            }

            var elapsed = now - entry.LastSeen;
            if (elapsed <= suspicionInterval)
            {
                continue;
            }

            var newStatus = elapsed >= leaveInterval ? MeshGossipMemberStatus.Left : MeshGossipMemberStatus.Suspect;
            if (entry.Status == newStatus)
            {
                continue;
            }

            var updated = entry with { Status = newStatus };
            _members.TryUpdate(entry.NodeId, updated, entry);
        }
    }

    /// <summary>Returns a snapshot of the cluster.</summary>
    public MeshGossipClusterView Snapshot()
    {
        var now = _timeProvider.GetUtcNow();
        var members = _members.Values
            .Select(state => state.ToSnapshot())
            .OrderByDescending(snapshot => snapshot.NodeId == _localNodeId)
            .ThenBy(static snapshot => snapshot.NodeId, StringComparer.Ordinal)
            .ToImmutableArray();

        return new MeshGossipClusterView(now, members, _localNodeId, MeshGossipOptions.CurrentSchemaVersion);
    }

    /// <summary>Picks candidate peers for a gossip round.</summary>
    public IReadOnlyList<MeshGossipMemberSnapshot> PickFanout(int fanout)
    {
        if (fanout <= 0)
        {
            return [];
        }

        var candidates = _members.Values
            .Where(state => state.NodeId != _localNodeId && state.Metadata.Endpoint is not null && state.Status != MeshGossipMemberStatus.Left)
            .Select(state => state.ToSnapshot())
            .ToArray();

        if (candidates.Length <= fanout)
        {
            return candidates;
        }

        var span = candidates.AsSpan();
        Shuffle(span);
        return span[..fanout].ToArray();
    }

    private static void Shuffle<T>(Span<T> values)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private sealed record MeshGossipMemberState(
        string NodeId,
        MeshGossipMemberMetadata Metadata,
        DateTimeOffset LastSeen,
        MeshGossipMemberStatus Status,
        double? RoundTripTimeMs)
    {
        public static MeshGossipMemberState CreateLocal(MeshGossipMemberMetadata metadata, DateTimeOffset now) =>
            new(metadata.NodeId, metadata, now, MeshGossipMemberStatus.Alive, RoundTripTimeMs: null);

        public static MeshGossipMemberState CreateFromSnapshot(MeshGossipMemberSnapshot snapshot, DateTimeOffset now, double? rtt) =>
            new(snapshot.NodeId, snapshot.Metadata, snapshot.LastSeen ?? now, snapshot.Status, rtt ?? snapshot.RoundTripTimeMs);

        public MeshGossipMemberState Merge(MeshGossipMemberSnapshot snapshot, DateTimeOffset now, double? rtt)
        {
            var metadata = Metadata;
            if (snapshot.Metadata.MetadataVersion >= metadata.MetadataVersion)
            {
                metadata = snapshot.Metadata;
            }

            var status = snapshot.Status switch
            {
                MeshGossipMemberStatus.Left => MeshGossipMemberStatus.Left,
                MeshGossipMemberStatus.Suspect when Status != MeshGossipMemberStatus.Left => MeshGossipMemberStatus.Suspect,
                _ => MeshGossipMemberStatus.Alive
            };

            var lastSeen = snapshot.LastSeen ?? now;
            var roundTrip = rtt ?? snapshot.RoundTripTimeMs ?? RoundTripTimeMs;
            return new MeshGossipMemberState(snapshot.NodeId, metadata, lastSeen, status, roundTrip);
        }

        public MeshGossipMemberSnapshot ToSnapshot() =>
            new()
            {
                NodeId = NodeId,
                Status = Status,
                LastSeen = LastSeen,
                RoundTripTimeMs = RoundTripTimeMs,
                Metadata = Metadata
            };
    }
}
