using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core.Shards;

namespace OmniRelay.ShardStore.ObjectStorage;

#pragma warning disable CA2007

/// <summary>Shard repository backed by a generalized object storage interface.</summary>
public sealed class ObjectStorageShardStore : IShardRepository
{
    private readonly IShardObjectStorage _storage;
    private readonly TimeProvider _timeProvider;
    private long _sequence;
    private readonly ConcurrentQueue<ShardRecordDiff> _diffs = new();

    public ObjectStorageShardStore(IShardObjectStorage storage, TimeProvider? timeProvider = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ShardRecord?> GetAsync(ShardKey key, CancellationToken cancellationToken = default)
    {
        var document = await _storage.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return document?.Record;
    }

    public async ValueTask<IReadOnlyList<ShardRecord>> ListAsync(string? namespaceId = null, CancellationToken cancellationToken = default)
    {
        var documents = await _storage.ListAsync(namespaceId, cancellationToken).ConfigureAwait(false);
        return documents.Select(doc => doc.Record).ToArray();
    }

    public async ValueTask<ShardMutationResult> UpsertAsync(ShardMutationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await _storage.GetAsync(request.Key, cancellationToken).ConfigureAwait(false);
        if (request.ExpectedVersion.HasValue)
        {
            var currentVersion = existing?.Record.Version ?? 0;
            if (request.ExpectedVersion.Value != currentVersion)
            {
                throw new ShardConcurrencyException($"Shard '{request.Key}' expected version {request.ExpectedVersion.Value} but was {currentVersion}.");
            }
        }

        var changeTicket = request.ChangeTicket ?? request.ChangeMetadata.ChangeTicket;
        var nextVersion = (existing?.Record.Version ?? 0) + 1;
        var now = _timeProvider.GetUtcNow();
        var checksum = ShardChecksum.Compute(
            request.Namespace,
            request.ShardId,
            request.StrategyId,
            request.OwnerNodeId,
            request.LeaderId,
            request.CapacityHint,
            request.Status,
            changeTicket);

        var record = new ShardRecord
        {
            Namespace = request.Namespace,
            ShardId = request.ShardId,
            StrategyId = request.StrategyId,
            OwnerNodeId = request.OwnerNodeId,
            LeaderId = request.LeaderId,
            CapacityHint = request.CapacityHint,
            Status = request.Status,
            Version = nextVersion,
            Checksum = checksum,
            UpdatedAt = now,
            ChangeTicket = changeTicket
        };

        var history = new ShardHistoryRecord
        {
            Namespace = request.Namespace,
            ShardId = request.ShardId,
            Version = nextVersion,
            StrategyId = request.StrategyId,
            Actor = request.ChangeMetadata.Actor,
            Reason = request.ChangeMetadata.Reason,
            ChangeTicket = changeTicket ?? request.ChangeMetadata.ChangeTicket,
            CreatedAt = now,
            OwnerNodeId = request.OwnerNodeId,
            PreviousOwnerNodeId = existing?.Record.OwnerNodeId,
            OwnershipDeltaPercent = request.ChangeMetadata.OwnershipDeltaPercent,
            Metadata = request.ChangeMetadata.Metadata
        };

        var historyList = existing?.History ?? new List<ShardHistoryRecord>();
        historyList.Add(history);

        var document = new ShardObjectDocument
        {
            Record = record,
            History = historyList
        };

        await _storage.UpsertAsync(document, cancellationToken).ConfigureAwait(false);

        var diff = new ShardRecordDiff(
            Position: Interlocked.Increment(ref _sequence),
            Current: record,
            Previous: existing?.Record,
            History: history);
        _diffs.Enqueue(diff);

        var created = existing is null;
        return new ShardMutationResult(record, history, created);
    }

    public async IAsyncEnumerable<ShardRecordDiff> StreamDiffsAsync(long? sinceVersion, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = _diffs.ToArray();
        foreach (var diff in snapshot)
        {
            if (sinceVersion.HasValue && diff.Position <= sinceVersion.Value)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return diff;
        }

        await Task.CompletedTask;
    }
}

#pragma warning restore CA2007
