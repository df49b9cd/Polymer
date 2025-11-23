using System.Collections.Immutable;
using System.Text.Json;
using Hugo;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Persists replication events as immutable blobs inside an object store (for example S3 or Azure Blob Storage).
/// </summary>
public sealed class ObjectStorageResourceLeaseReplicator : IResourceLeaseReplicator, IDisposable
{
    private readonly IResourceLeaseObjectStore _objectStore;
    private readonly string _keyPrefix;
    private readonly ImmutableArray<IResourceLeaseReplicationSink> _sinks;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private long _sequenceNumber;
    private bool _initialized;

    public ObjectStorageResourceLeaseReplicator(
        IResourceLeaseObjectStore objectStore,
        string keyPrefix = "resourcelease/",
        IEnumerable<IResourceLeaseReplicationSink>? sinks = null)
    {
        ArgumentNullException.ThrowIfNull(objectStore);
        _objectStore = objectStore;
        _keyPrefix = keyPrefix ?? string.Empty;
        _sinks = sinks is null
            ? []
            : [.. sinks.Where(s => s is not null)];
    }

    public async ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (replicationEvent is null)
        {
            return Err<Unit>(ResourceLeaseReplicationErrors.EventRequired("objectstorage.publish"));
        }

        var initialized = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (initialized.IsFailure)
        {
            return initialized;
        }

        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            Timestamp = DateTimeOffset.UtcNow
        };

        var key = $"{_keyPrefix}{ordered.SequenceNumber:D20}.json";
        var payload = JsonSerializer.SerializeToUtf8Bytes(ordered, ResourceLeaseJsonContext.Default.ResourceLeaseReplicationEvent);
        var persisted = await PersistAsync(key, payload, ordered, cancellationToken).ConfigureAwait(false);
        if (persisted.IsFailure)
        {
            return persisted;
        }

        return await FanOutAsync(ordered, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<Unit>> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return Ok(Unit.Value);
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return Ok(Unit.Value);
            }

            var keys = await _objectStore.ListKeysAsync(_keyPrefix, cancellationToken).ConfigureAwait(false);
            if (keys.Count > 0)
            {
                var last = keys.OrderBy(k => k, StringComparer.Ordinal).Last();
                if (TryParseSequence(last, out var seq))
                {
                    _sequenceNumber = seq;
                }
            }

            _initialized = true;
            return Ok(Unit.Value);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<Unit>(Error.Canceled("object storage replication initialization canceled", cancellationToken)
                .WithMetadata("replication.stage", "objectstorage.initialize")
                .WithMetadata("replication.prefix", _keyPrefix));
        }
        catch (Exception ex)
        {
            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", "objectstorage.initialize")
                .WithMetadata("replication.prefix", _keyPrefix));
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async ValueTask<Result<Unit>> PersistAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        ResourceLeaseReplicationEvent replicationEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _objectStore.WriteAsync(key, payload, "application/json", cancellationToken).ConfigureAwait(false);
            return Ok(Unit.Value);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<Unit>(Error.Canceled("object storage replication persist canceled", cancellationToken)
                .WithMetadata("replication.stage", "objectstorage.persist")
                .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                .WithMetadata("replication.key", key));
        }
        catch (Exception ex)
        {
            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", "objectstorage.persist")
                .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                .WithMetadata("replication.key", key));
        }
    }

    private async ValueTask<Result<Unit>> FanOutAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (_sinks.IsDefaultOrEmpty)
        {
            return Ok(Unit.Value);
        }

        foreach (var sink in _sinks)
        {
            try
            {
                var applied = await sink.ApplyAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
                if (applied.IsFailure)
                {
                    return Err<Unit>(applied.Error!
                        .WithMetadata("replication.stage", "objectstorage.sink")
                        .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                        .WithMetadata("replication.sink", sink.GetType().Name));
                }
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
            {
                return Err<Unit>(Error.Canceled("object storage sink canceled", cancellationToken)
                    .WithMetadata("replication.stage", "objectstorage.sink")
                    .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                    .WithMetadata("replication.sink", sink.GetType().Name));
            }
            catch (Exception ex)
            {
                return Err<Unit>(Error.FromException(ex)
                    .WithMetadata("replication.stage", "objectstorage.sink")
                    .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                    .WithMetadata("replication.sink", sink.GetType().Name));
            }
        }

        return Ok(Unit.Value);
    }

    private bool TryParseSequence(string key, out long sequence)
    {
        sequence = 0;
        var fileName = Path.GetFileNameWithoutExtension(key.AsSpan());
        if (!fileName.StartsWith(_keyPrefix.AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        var sequenceSpan = fileName[_keyPrefix.Length..];
        return long.TryParse(sequenceSpan, out sequence);
    }

    public void Dispose()
    {
        _initializationGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
