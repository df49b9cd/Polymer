using Hugo;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class ResourceLeaseReplicationTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InMemoryReplicator_SequencesAndDeliversEvents()
    {
        var sink = new RecordingSink();
        var replicator = new InMemoryResourceLeaseReplicator([sink]);

        var first = await replicator.PublishAsync(CreateEvent(), CancellationToken.None);
        var second = await replicator.PublishAsync(CreateEvent(), CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(second.IsSuccess, second.Error?.ToString());

        Assert.Equal([1L, 2L], [.. sink.Events.Select(evt => evt.SequenceNumber)]);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InMemoryReplicator_IgnoresProvidedSequenceAndUsesStartingOffset()
    {
        var sink = new RecordingSink();
        var replicator = new InMemoryResourceLeaseReplicator([sink], startingSequence: 10);

        var result = await replicator.PublishAsync(CreateEvent(sequence: 42), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(sink.Events);
        Assert.Equal(11L, sink.Events[0].SequenceNumber);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CheckpointingSink_DeduplicatesSequences()
    {
        var sink = new CountingCheckpointSink();

        var first = await sink.ApplyAsync(CreateEvent(sequence: 5), CancellationToken.None);
        var duplicate = await sink.ApplyAsync(CreateEvent(sequence: 5), CancellationToken.None);
        var second = await sink.ApplyAsync(CreateEvent(sequence: 6), CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(duplicate.IsSuccess, duplicate.Error?.ToString());
        Assert.True(second.IsSuccess, second.Error?.ToString());

        Assert.Equal(new[] { 5L, 6L }, sink.AppliedSequences);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask DeterministicCoordinator_IgnoresDuplicateEffects()
    {
        var coordinatorResult = DeterministicResourceLeaseCoordinator.Create(new ResourceLeaseDeterministicOptions
        {
            ChangeId = "leases",
            MinVersion = 1,
            MaxVersion = 1,
            StateStore = new InMemoryDeterministicStateStore()
        });
        Assert.True(coordinatorResult.IsSuccess, coordinatorResult.Error?.ToString());
        var coordinator = coordinatorResult.Value;

        var evt = CreateEvent(sequence: 10);

        var first = await coordinator.RecordAsync(evt, CancellationToken.None);
        var duplicate = await coordinator.RecordAsync(evt, CancellationToken.None); // duplicate should be ignored without throwing

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(duplicate.IsSuccess, duplicate.Error?.ToString());
    }

    private static ResourceLeaseReplicationEvent CreateEvent(long sequence = 0) =>
        new(
            sequence,
            ResourceLeaseReplicationEventType.LeaseGranted,
            DateTimeOffset.UtcNow,
            new ResourceLeaseOwnershipHandle(5, 1, Guid.NewGuid()),
            "peer-a",
            new ResourceLeaseItemPayload("workflow", "job-123", "pk", "json", []),
            null,
            []);

    private sealed class RecordingSink : IResourceLeaseReplicationSink
    {
        public List<ResourceLeaseReplicationEvent> Events { get; } = [];

        public ValueTask<Result<Unit>> ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            Events.Add(replicationEvent);
            return ValueTask.FromResult(Ok(Unit.Value));
        }
    }

    private sealed class CountingCheckpointSink : CheckpointingResourceLeaseReplicationSink
    {
        public List<long> AppliedSequences { get; } = [];

        protected override ValueTask<Result<Unit>> ApplyInternalAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            AppliedSequences.Add(replicationEvent.SequenceNumber);
            return ValueTask.FromResult(Ok(Unit.Value));
        }
    }
}
