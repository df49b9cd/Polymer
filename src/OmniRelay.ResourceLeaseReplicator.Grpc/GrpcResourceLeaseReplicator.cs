using System.Collections.Immutable;
using Hugo;
using OmniRelay.Dispatcher.Grpc;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Replicates events by forwarding them to a remote gRPC service and optionally applying local sinks.
/// </summary>
public sealed class GrpcResourceLeaseReplicator : IResourceLeaseReplicator
{
    private readonly IGrpcResourceLeaseReplicatorClient _client;
    private readonly ImmutableArray<IResourceLeaseReplicationSink> _sinks;
    private long _sequenceNumber;

    public GrpcResourceLeaseReplicator(
        ResourceLeaseReplicatorGrpc.ResourceLeaseReplicatorGrpcClient client,
        IEnumerable<IResourceLeaseReplicationSink>? sinks = null,
        long startingSequence = 0)
        : this(new GrpcResourceLeaseReplicatorClientAdapter(client), sinks, startingSequence)
    {
    }

    public GrpcResourceLeaseReplicator(
            IGrpcResourceLeaseReplicatorClient client,
            IEnumerable<IResourceLeaseReplicationSink>? sinks = null,
            long startingSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _sequenceNumber = startingSequence;
        _sinks = sinks is null
            ? []
            : [.. sinks.Where(s => s is not null)];
    }

    public async ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (replicationEvent is null)
        {
            return Err<Unit>(ResourceLeaseReplicationErrors.EventRequired("grpc.publish"));
        }

        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            Timestamp = DateTimeOffset.UtcNow
        };

        ResourceLeaseReplicationEventMessage message;
        try
        {
            message = ResourceLeaseReplicationGrpcMapper.ToMessage(ordered);
        }
        catch (Exception ex)
        {
            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", "grpc.map.to_message")
                .WithMetadata("replication.sequence", ordered.SequenceNumber));
        }

        var published = await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        if (published.IsFailure)
        {
            return Err<Unit>(published.Error!
                .WithMetadata("replication.stage", "grpc.publish")
                .WithMetadata("replication.sequence", ordered.SequenceNumber));
        }

        return await FanOutAsync(ordered, cancellationToken).ConfigureAwait(false);
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
                        .WithMetadata("replication.stage", "grpc.sink")
                        .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                        .WithMetadata("replication.sink", sink.GetType().Name));
                }
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
            {
                return Err<Unit>(Error.Canceled("grpc sink canceled", cancellationToken)
                    .WithMetadata("replication.stage", "grpc.sink")
                    .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                    .WithMetadata("replication.sink", sink.GetType().Name));
            }
            catch (Exception ex)
            {
                return Err<Unit>(Error.FromException(ex)
                    .WithMetadata("replication.stage", "grpc.sink")
                    .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                    .WithMetadata("replication.sink", sink.GetType().Name));
            }
        }

        return Ok(Unit.Value);
    }
}
