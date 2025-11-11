using System.Collections.Immutable;
using OmniRelay.Dispatcher.Grpc;

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
            ? ImmutableArray<IResourceLeaseReplicationSink>.Empty
            : [.. sinks.Where(s => s is not null)];
    }

    public async ValueTask PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replicationEvent);

        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            Timestamp = DateTimeOffset.UtcNow
        };

        var message = ResourceLeaseReplicationGrpcMapper.ToMessage(ordered);
        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        await FanOutAsync(ordered, cancellationToken).ConfigureAwait(false);
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
}
