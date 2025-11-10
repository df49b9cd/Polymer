using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Persists replication events as immutable blobs inside an object store (for example S3 or Azure Blob Storage).
/// </summary>
public sealed class ObjectStorageResourceLeaseReplicator : IResourceLeaseReplicator
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
            ? ImmutableArray<IResourceLeaseReplicationSink>.Empty
            : sinks.Where(s => s is not null).ToImmutableArray();
    }

    public async ValueTask PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replicationEvent);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            Timestamp = DateTimeOffset.UtcNow
        };

        var key = $"{_keyPrefix}{ordered.SequenceNumber:D20}.json";
        var payload = JsonSerializer.SerializeToUtf8Bytes(ordered, ResourceLeaseJson.Options);
        await _objectStore.WriteAsync(key, payload, "application/json", cancellationToken).ConfigureAwait(false);
        await FanOutAsync(ordered, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
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
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task FanOutAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (_sinks.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var sink in _sinks)
        {
            await sink.ApplyAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
        }
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
}
