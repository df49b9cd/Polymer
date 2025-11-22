namespace OmniRelay.Core.Shards;

/// <summary>Abstraction over the shard persistence layer.</summary>
public interface IShardRepository
{
    ValueTask<ShardRecord?> GetAsync(ShardKey key, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ShardRecord>> ListAsync(string? namespaceId = null, CancellationToken cancellationToken = default);

    ValueTask<ShardMutationResult> UpsertAsync(ShardMutationRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ShardRecordDiff> StreamDiffsAsync(long? sinceVersion, CancellationToken cancellationToken = default);

    ValueTask<ShardQueryResult> QueryAsync(ShardQueryOptions options, CancellationToken cancellationToken = default);
}
