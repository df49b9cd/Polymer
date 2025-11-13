using OmniRelay.Core.Shards;

namespace OmniRelay.ShardStore.ObjectStorage;

/// <summary>Abstraction for storing shard documents inside an object storage system.</summary>
public interface IShardObjectStorage
{
    Task<ShardObjectDocument?> GetAsync(ShardKey key, CancellationToken cancellationToken);

    Task<IReadOnlyList<ShardObjectDocument>> ListAsync(string? namespaceId, CancellationToken cancellationToken);

    Task UpsertAsync(ShardObjectDocument document, CancellationToken cancellationToken);
}
