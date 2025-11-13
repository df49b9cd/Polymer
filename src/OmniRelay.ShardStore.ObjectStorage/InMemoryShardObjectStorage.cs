using System.Collections.Concurrent;
using OmniRelay.Core.Shards;

namespace OmniRelay.ShardStore.ObjectStorage;

/// <summary>In-memory shard document store useful for tests and local development.</summary>
public sealed class InMemoryShardObjectStorage : IShardObjectStorage
{
    private readonly ConcurrentDictionary<ShardKey, ShardObjectDocument> _documents = new();

    public Task<ShardObjectDocument?> GetAsync(ShardKey key, CancellationToken cancellationToken)
    {
        _documents.TryGetValue(key, out var document);
        return Task.FromResult(document);
    }

    public Task<IReadOnlyList<ShardObjectDocument>> ListAsync(string? namespaceId, CancellationToken cancellationToken)
    {
        IEnumerable<KeyValuePair<ShardKey, ShardObjectDocument>> source = _documents;
        if (!string.IsNullOrWhiteSpace(namespaceId))
        {
            source = source.Where(entry => string.Equals(entry.Key.Namespace, namespaceId, StringComparison.OrdinalIgnoreCase));
        }

        var results = source.Select(entry => entry.Value).ToArray();
        return Task.FromResult<IReadOnlyList<ShardObjectDocument>>(results);
    }

    public Task UpsertAsync(ShardObjectDocument document, CancellationToken cancellationToken)
    {
        _documents[document.Record.Key] = document;
        return Task.CompletedTask;
    }
}
