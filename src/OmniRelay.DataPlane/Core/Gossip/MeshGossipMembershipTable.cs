using System.Buffers;
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

        if (string.Equals(snapshot.NodeId, _localNodeId, StringComparison.Ordinal))
        {
            _members.AddOrUpdate(
                snapshot.NodeId,
                _ => MeshGossipMemberState.CreateLocal(snapshot.Metadata ?? LocalMetadata, now),
                (_, existing) =>
                {
                    var metadata = existing.Metadata;
                    var incomingMetadata = snapshot.Metadata;
                    if (incomingMetadata is not null && incomingMetadata.MetadataVersion >= metadata.MetadataVersion)
                    {
                        metadata = incomingMetadata;
                    }

                    return existing with
                    {
                        Metadata = metadata,
                        LastSeen = now,
                        Status = MeshGossipMemberStatus.Alive
                    };
                });

            return;
        }

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

    /// <summary>Picks candidate peers for a gossip round.</summary>
    public IReadOnlyList<MeshGossipMemberSnapshot> PickFanout(int fanout)
    {
        if (fanout <= 0)
        {
            return [];
        }

        var pool = ArrayPool<MeshGossipMemberState>.Shared;
        var reservoir = pool.Rent(fanout);
        var seen = 0;
        var selected = 0;

        try
        {
            foreach (var state in _members.Values)
            {
                if (state.NodeId == _localNodeId || state.Status == MeshGossipMemberStatus.Left)
                {
                    continue;
                }

                if (selected < fanout)
                {
                    reservoir[selected++] = state;
                }
                else
                {
                    var replaceIndex = Random.Shared.Next(seen + 1);
                    if (replaceIndex < fanout)
                    {
                        reservoir[replaceIndex] = state;
                    }
                }

                seen++;
            }

            if (selected == 0)
            {
                return Array.Empty<MeshGossipMemberSnapshot>();
            }

            var take = Math.Min(fanout, selected);
            var span = reservoir.AsSpan(0, take);
            Shuffle(span);

            var result = new MeshGossipMemberSnapshot[take];
            for (var i = 0; i < take; i++)
            {
                result[i] = span[i].ToSnapshot();
            }

            return result;
        }
        finally
        {
            reservoir.AsSpan(0, Math.Min(selected, reservoir.Length)).Clear();
            pool.Return(reservoir);
        }
    }

    private static void Shuffle<T>(Span<T> values)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>Returns a snapshot of the cluster.</summary>
    public MeshGossipClusterView Snapshot()
    {
        var now = _timeProvider.GetUtcNow();
        var pool = ArrayPool<MeshGossipMemberSnapshot>.Shared;
        var buffer = pool.Rent(Math.Max(4, _members.Count));
        var count = 0;

        try
        {
            foreach (var state in _members.Values)
            {
                if (count == buffer.Length)
                {
                    var expanded = pool.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, count).CopyTo(expanded);
                    pool.Return(buffer, clearArray: true);
                    buffer = expanded;
                }

                buffer[count++] = state.ToSnapshot();
            }

            Array.Sort(buffer, 0, count, new LocalFirstComparer(_localNodeId));

            var builder = ImmutableArray.CreateBuilder<MeshGossipMemberSnapshot>(count);
            builder.AddRange(buffer.AsSpan(0, count));

            return new MeshGossipClusterView(now, builder.MoveToImmutable(), _localNodeId, MeshGossipOptions.CurrentSchemaVersion);
        }
        finally
        {
            buffer.AsSpan(0, count).Clear();
            pool.Return(buffer);
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

    private sealed class LocalFirstComparer(string localNodeId) : IComparer<MeshGossipMemberSnapshot>
    {
        public int Compare(MeshGossipMemberSnapshot? x, MeshGossipMemberSnapshot? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var xIsLocal = string.Equals(x.NodeId, localNodeId, StringComparison.Ordinal);
            var yIsLocal = string.Equals(y.NodeId, localNodeId, StringComparison.Ordinal);

            if (xIsLocal && !yIsLocal)
            {
                return -1;
            }

            if (yIsLocal && !xIsLocal)
            {
                return 1;
            }

            return string.CompareOrdinal(x.NodeId, y.NodeId);
        }
    }
}
