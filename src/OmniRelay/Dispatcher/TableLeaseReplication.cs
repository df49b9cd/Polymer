using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace OmniRelay.Dispatcher;

/// <summary>Represents an ordered replication event emitted whenever the table lease queue mutates.</summary>
public sealed record TableLeaseReplicationEvent(
    long SequenceNumber,
    TableLeaseReplicationEventType EventType,
    DateTimeOffset Timestamp,
    TableLeaseOwnershipHandle? Ownership,
    string? PeerId,
    TableLeaseItemPayload? Payload,
    TableLeaseErrorInfo? Error,
    ImmutableDictionary<string, string> Metadata)
{
    public static TableLeaseReplicationEvent Create(
        TableLeaseReplicationEventType eventType,
        TableLeaseOwnershipHandle? ownership,
        string? peerId,
        TableLeaseItemPayload? payload,
        TableLeaseErrorInfo? error,
        IReadOnlyDictionary<string, string>? metadata) =>
        new(
            SequenceNumber: 0,
            eventType,
            Timestamp: DateTimeOffset.UtcNow,
            ownership,
            peerId,
            payload,
            error,
            metadata is null
                ? ImmutableDictionary<string, string>.Empty
                : metadata.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Enumerates table lease event types replicated across metadata nodes.</summary>
public enum TableLeaseReplicationEventType
{
    Enqueue = 1,
    LeaseGranted = 2,
    Heartbeat = 3,
    Completed = 4,
    Failed = 5,
    DrainSnapshot = 6,
    RestoreSnapshot = 7
}

/// <summary>Replicates ordered table lease events to downstream sinks.</summary>
public interface ITableLeaseReplicator
{
    ValueTask PublishAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}

/// <summary>Consumers implement this interface to apply ordered replication events and optionally checkpoint progress.</summary>
public interface ITableLeaseReplicationSink
{
    ValueTask ApplyAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}

/// <summary>
/// In-memory hub that sequences replication events and fan-outs to registered sinks with basic deduplication guarantees.
/// </summary>
public sealed class InMemoryTableLeaseReplicator : ITableLeaseReplicator
{
    private readonly List<ITableLeaseReplicationSink> _sinks = new();
    private long _sequenceId;

    public InMemoryTableLeaseReplicator(IEnumerable<ITableLeaseReplicationSink>? sinks = null, long startingSequence = 0)
    {
        _sequenceId = startingSequence;
        if (sinks is not null)
        {
            _sinks.AddRange(sinks.Where(s => s is not null));
        }
    }

    public void RegisterSink(ITableLeaseReplicationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_sinks)
        {
            _sinks.Add(sink);
        }
    }

    public async ValueTask PublishAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceId),
            Timestamp = DateTimeOffset.UtcNow
        };

        ITableLeaseReplicationSink[] sinks;
        lock (_sinks)
        {
            sinks = _sinks.ToArray();
        }

        foreach (var sink in sinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sink.ApplyAsync(ordered, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Base sink that ensures events are applied exactly once by tracking the last processed sequence number per peer.
/// </summary>
public abstract class CheckpointingTableLeaseReplicationSink : ITableLeaseReplicationSink
{
    private readonly ConcurrentDictionary<string, long> _peerCheckpoints = new(StringComparer.Ordinal);
    private long _globalCheckpoint = -1;

    public async ValueTask ApplyAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (replicationEvent.SequenceNumber <= Volatile.Read(ref _globalCheckpoint))
        {
            return;
        }

        if (replicationEvent.PeerId is { Length: > 0 } peer)
        {
            var lastPeerSequence = _peerCheckpoints.GetOrAdd(peer, static _ => -1);
            if (replicationEvent.SequenceNumber <= lastPeerSequence)
            {
                return;
            }
        }

        await ApplyInternalAsync(replicationEvent, cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _globalCheckpoint, replicationEvent.SequenceNumber);
        if (replicationEvent.PeerId is { Length: > 0 } owner)
        {
            _peerCheckpoints.AddOrUpdate(owner, replicationEvent.SequenceNumber, (_, _) => replicationEvent.SequenceNumber);
        }
    }

    protected abstract ValueTask ApplyInternalAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}
