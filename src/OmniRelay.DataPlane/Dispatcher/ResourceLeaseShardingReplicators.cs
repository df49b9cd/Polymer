using System.Collections.Immutable;
using Hugo;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

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

    public async ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (replicationEvent is null)
        {
            return Err<Unit>(ResourceLeaseReplicationErrors.EventRequired("shard.publish"));
        }

        var metadata = replicationEvent.Metadata.SetItem("shard.id", _shardId);
        var tagged = replicationEvent with { Metadata = metadata };
        try
        {
            return await _inner.PublishAsync(tagged, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<Unit>(Error.Canceled("sharded replication canceled", cancellationToken)
                .WithMetadata("replication.stage", "shard.forward")
                .WithMetadata("replication.shard", _shardId));
        }
        catch (Exception ex)
        {
            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", "shard.forward")
                .WithMetadata("replication.shard", _shardId));
        }
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

    public async ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (replicationEvent is null)
        {
            return Err<Unit>(ResourceLeaseReplicationErrors.EventRequired("composite.publish"));
        }

        foreach (var replicator in _replicators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var published = await replicator.PublishAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
                if (published.IsFailure)
                {
                    return Err<Unit>(published.Error!
                        .WithMetadata("replication.stage", "composite.publish")
                        .WithMetadata("replication.replicator", replicator.GetType().Name));
                }
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
            {
                return Err<Unit>(Error.Canceled("composite replication canceled", cancellationToken)
                    .WithMetadata("replication.stage", "composite.publish")
                    .WithMetadata("replication.replicator", replicator.GetType().Name));
            }
            catch (Exception ex)
            {
                return Err<Unit>(Error.FromException(ex)
                    .WithMetadata("replication.stage", "composite.publish")
                    .WithMetadata("replication.replicator", replicator.GetType().Name));
            }
        }

        return Ok(Unit.Value);
    }
}
