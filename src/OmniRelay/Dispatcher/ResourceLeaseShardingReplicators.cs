using System.Collections.Immutable;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Decorator that annotates every replication event with a shard identifier before forwarding to the inner replicator.
/// </summary>
public sealed class ShardedResourceLeaseReplicator : IResourceLeaseReplicator
{
    private readonly IResourceLeaseReplicator _inner;
    private readonly string _shardId;

    public ShardedResourceLeaseReplicator(IResourceLeaseReplicator inner, string shardId)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;

        if (string.IsNullOrWhiteSpace(shardId))
        {
            throw new ArgumentException("Shard id must be provided.", nameof(shardId));
        }

        _shardId = shardId.Trim();
    }

    public ValueTask PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replicationEvent);

        var metadata = replicationEvent.Metadata.SetItem("shard.id", _shardId);
        var tagged = replicationEvent with { Metadata = metadata };
        return _inner.PublishAsync(tagged, cancellationToken);
    }
}

/// <summary>
/// Fan-out replicator that forwards every event to multiple inner replicators (for example per-shard hubs or sinks).
/// </summary>
public sealed class CompositeResourceLeaseReplicator : IResourceLeaseReplicator
{
    private readonly ImmutableArray<IResourceLeaseReplicator> _replicators;

    public CompositeResourceLeaseReplicator(IEnumerable<IResourceLeaseReplicator> replicators)
    {
        ArgumentNullException.ThrowIfNull(replicators);

        var snapshot = replicators.Where(r => r is not null).ToImmutableArray();
        if (snapshot.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one replicator must be supplied.", nameof(replicators));
        }

        _replicators = snapshot;
    }

    public async ValueTask PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replicationEvent);

        foreach (var replicator in _replicators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await replicator.PublishAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
