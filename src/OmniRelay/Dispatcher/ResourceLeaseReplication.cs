using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace OmniRelay.Dispatcher;

/// <summary>Represents an ordered replication event emitted whenever the resource lease queue mutates.</summary>
public sealed record ResourceLeaseReplicationEvent(
    long SequenceNumber,
    ResourceLeaseReplicationEventType EventType,
    DateTimeOffset Timestamp,
    ResourceLeaseOwnershipHandle? Ownership,
    string? PeerId,
    ResourceLeaseItemPayload? Payload,
    ResourceLeaseErrorInfo? Error,
    ImmutableDictionary<string, string> Metadata)
{
    public static ResourceLeaseReplicationEvent Create(
        ResourceLeaseReplicationEventType eventType,
        ResourceLeaseOwnershipHandle? ownership,
        string? peerId,
        ResourceLeaseItemPayload? payload,
        ResourceLeaseErrorInfo? error,
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

    public long SequenceNumber { get; init; } = SequenceNumber;

    public ResourceLeaseReplicationEventType EventType { get; init; } = EventType;

    public DateTimeOffset Timestamp { get; init; } = Timestamp;

    public ResourceLeaseOwnershipHandle? Ownership { get; init; } = Ownership;

    public string? PeerId { get; init; } = PeerId;

    public ResourceLeaseItemPayload? Payload { get; init; } = Payload;

    public ResourceLeaseErrorInfo? Error { get; init; } = Error;

    public ImmutableDictionary<string, string> Metadata { get; init; } = Metadata;
}

/// <summary>Enumerates resource lease event types replicated across metadata nodes.</summary>
public enum ResourceLeaseReplicationEventType
{
    Enqueue = 1,
    LeaseGranted = 2,
    Heartbeat = 3,
    Completed = 4,
    Failed = 5,
    DrainSnapshot = 6,
    RestoreSnapshot = 7
}

/// <summary>Replicates ordered resource lease events to downstream sinks.</summary>
public interface IResourceLeaseReplicator
{
    ValueTask PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}

/// <summary>Consumers implement this interface to apply ordered replication events and optionally checkpoint progress.</summary>
public interface IResourceLeaseReplicationSink
{
    ValueTask ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}

/// <summary>
/// In-memory hub that sequences replication events and fan-outs to registered sinks with basic deduplication guarantees.
/// </summary>
public sealed class InMemoryResourceLeaseReplicator : IResourceLeaseReplicator
{
    private readonly List<IResourceLeaseReplicationSink> _sinks = [];
    private long _sequenceId;

    public InMemoryResourceLeaseReplicator(IEnumerable<IResourceLeaseReplicationSink>? sinks = null, long startingSequence = 0)
    {
        _sequenceId = startingSequence;
        if (sinks is not null)
        {
            _sinks.AddRange(sinks.Where(s => s is not null));
        }
    }

    public void RegisterSink(IResourceLeaseReplicationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_sinks)
        {
            _sinks.Add(sink);
        }
    }

    public async ValueTask PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceId),
            Timestamp = DateTimeOffset.UtcNow
        };

        IResourceLeaseReplicationSink[] sinks;
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
public abstract class CheckpointingResourceLeaseReplicationSink : IResourceLeaseReplicationSink
{
    private readonly ConcurrentDictionary<string, long> _peerCheckpoints = new(StringComparer.Ordinal);
    private long _globalCheckpoint = -1;

    public async ValueTask ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
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

        var lagMilliseconds = (DateTimeOffset.UtcNow - replicationEvent.Timestamp).TotalMilliseconds;
        ResourceLeaseReplicationMetrics.RecordReplicationLag(replicationEvent, lagMilliseconds);

        await ApplyInternalAsync(replicationEvent, cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _globalCheckpoint, replicationEvent.SequenceNumber);
        if (replicationEvent.PeerId is { Length: > 0 } owner)
        {
            _peerCheckpoints.AddOrUpdate(owner, replicationEvent.SequenceNumber, (_, _) => replicationEvent.SequenceNumber);
        }
    }

    protected abstract ValueTask ApplyInternalAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}
