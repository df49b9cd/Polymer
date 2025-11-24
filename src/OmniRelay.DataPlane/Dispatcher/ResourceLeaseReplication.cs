using System.Collections.Concurrent;
using System.Collections.Immutable;
using Hugo;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

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
                ? []
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
    ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}

/// <summary>Consumers implement this interface to apply ordered replication events and optionally checkpoint progress.</summary>
public interface IResourceLeaseReplicationSink
{
    ValueTask<Result<Unit>> ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}

public static class ResourceLeaseReplicationErrors
{
    public static Error EventRequired(string? stage = null)
    {
        IReadOnlyDictionary<string, object?>? metadata = null;
        if (stage is not null)
        {
            metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["replication.stage"] = stage
            };
        }

        return Error.From(
            "Replication event is required.",
            "error.resourcelease.replication.event_required",
            cause: null!,
            metadata: (IReadOnlyDictionary<string, object?>?)metadata);
    }

    public static Error ShardIdMissing() =>
        Error.From(
            "Shard id must be provided.",
            "error.resourcelease.replication.shard_id_missing",
            cause: null!,
            metadata: (IReadOnlyDictionary<string, object?>?)null);
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

    public async ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (replicationEvent is null)
        {
            return Err<Unit>(ResourceLeaseReplicationErrors.EventRequired("inmemory.publish"));
        }

        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceId),
            Timestamp = DateTimeOffset.UtcNow
        };

        IResourceLeaseReplicationSink[] sinks;
        lock (_sinks)
        {
            sinks = [.. _sinks];
        }

        foreach (var sink in sinks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Err<Unit>(Error.Canceled("in-memory replication canceled", cancellationToken)
                    .WithMetadata("replication.stage", "inmemory.publish"));
            }

            try
            {
                var applied = await sink.ApplyAsync(ordered, cancellationToken).ConfigureAwait(false);
                if (applied.IsFailure)
                {
                    return Err<Unit>(applied.Error!
                        .WithMetadata("replication.stage", "inmemory.sink")
                        .WithMetadata("replication.sequence", ordered.SequenceNumber)
                        .WithMetadata("replication.sink", sink.GetType().Name));
                }
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
            {
                return Err<Unit>(Error.Canceled("in-memory sink canceled", cancellationToken)
                    .WithMetadata("replication.stage", "inmemory.sink")
                    .WithMetadata("replication.sequence", ordered.SequenceNumber)
                    .WithMetadata("replication.sink", sink.GetType().Name));
            }
            catch (Exception ex)
            {
                return Err<Unit>(Error.FromException(ex)
                    .WithMetadata("replication.stage", "inmemory.sink")
                    .WithMetadata("replication.sequence", ordered.SequenceNumber)
                    .WithMetadata("replication.sink", sink.GetType().Name));
            }
        }

        return Ok(Unit.Value);
    }
}

/// <summary>
/// Base sink that ensures events are applied exactly once by tracking the last processed sequence number per peer.
/// </summary>
public abstract class CheckpointingResourceLeaseReplicationSink : IResourceLeaseReplicationSink
{
    private readonly ConcurrentDictionary<string, long> _peerCheckpoints = new(StringComparer.Ordinal);
    private long _globalCheckpoint = -1;

    public async ValueTask<Result<Unit>> ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (replicationEvent is null)
        {
            return Err<Unit>(ResourceLeaseReplicationErrors.EventRequired("sink.apply"));
        }

        if (replicationEvent.SequenceNumber <= Volatile.Read(ref _globalCheckpoint))
        {
            return Ok(Unit.Value);
        }

        if (replicationEvent.PeerId is { Length: > 0 } peer)
        {
            var lastPeerSequence = _peerCheckpoints.GetOrAdd(peer, static _ => -1);
            if (replicationEvent.SequenceNumber <= lastPeerSequence)
            {
                return Ok(Unit.Value);
            }
        }

        var lagMilliseconds = (DateTimeOffset.UtcNow - replicationEvent.Timestamp).TotalMilliseconds;
        ResourceLeaseReplicationMetrics.RecordReplicationLag(replicationEvent, lagMilliseconds);

        Result<Unit> applied;
        try
        {
            applied = await ApplyInternalAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<Unit>(Error.Canceled("replication sink canceled", cancellationToken)
                .WithMetadata("replication.stage", "sink.apply_internal")
                .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                .WithMetadata("replication.peerId", replicationEvent.PeerId ?? string.Empty));
        }
        catch (Exception ex)
        {
            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", "sink.apply_internal")
                .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                .WithMetadata("replication.peerId", replicationEvent.PeerId ?? string.Empty));
        }

        if (applied.IsFailure)
        {
            return Err<Unit>(applied.Error!
                .WithMetadata("replication.stage", "sink.apply_internal")
                .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                .WithMetadata("replication.peerId", replicationEvent.PeerId ?? string.Empty));
        }

        Interlocked.Exchange(ref _globalCheckpoint, replicationEvent.SequenceNumber);
        if (replicationEvent.PeerId is { Length: > 0 } owner)
        {
            _peerCheckpoints.AddOrUpdate(owner, replicationEvent.SequenceNumber, (_, _) => replicationEvent.SequenceNumber);
        }

        return Ok(Unit.Value);
    }

    protected abstract ValueTask<Result<Unit>> ApplyInternalAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}
