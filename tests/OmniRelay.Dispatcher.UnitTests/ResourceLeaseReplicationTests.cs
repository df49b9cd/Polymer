using AwesomeAssertions;
using Hugo;
using Xunit;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

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

        first.IsSuccess.Should().BeTrue(first.Error?.ToString());
        second.IsSuccess.Should().BeTrue(second.Error?.ToString());

        sink.Events.Select(evt => evt.SequenceNumber).Should().Equal(1L, 2L);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InMemoryReplicator_IgnoresProvidedSequenceAndUsesStartingOffset()
    {
        var sink = new RecordingSink();
        var replicator = new InMemoryResourceLeaseReplicator([sink], startingSequence: 10);

        var result = await replicator.PublishAsync(CreateEvent(sequence: 42), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error?.ToString());
        sink.Events.Should().ContainSingle();
        sink.Events[0].SequenceNumber.Should().Be(11L);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InMemoryReplicator_PropagatesSinkFailure()
    {
        var failing = new DelegatingSink(() => Err<Unit>(Error.From("failed", "sink.failed")));
        var replicator = new InMemoryResourceLeaseReplicator([failing]);

        var result = await replicator.PublishAsync(CreateEvent(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Metadata.Should().ContainKey("replication.stage");
        result.Error!.Metadata["replication.stage"].Should().Be("inmemory.sink");
        result.Error!.Metadata.Should().ContainKey("replication.sink");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InMemoryReplicator_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sink = new RecordingSink();
        var replicator = new InMemoryResourceLeaseReplicator([sink]);

        var result = await replicator.PublishAsync(CreateEvent(), cts.Token);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ErrorCodes.Canceled);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CheckpointingSink_DeduplicatesSequences()
    {
        var sink = new CountingCheckpointSink();

        var first = await sink.ApplyAsync(CreateEvent(sequence: 5), CancellationToken.None);
        var duplicate = await sink.ApplyAsync(CreateEvent(sequence: 5), CancellationToken.None);
        var second = await sink.ApplyAsync(CreateEvent(sequence: 6), CancellationToken.None);

        first.IsSuccess.Should().BeTrue(first.Error?.ToString());
        duplicate.IsSuccess.Should().BeTrue(duplicate.Error?.ToString());
        second.IsSuccess.Should().BeTrue(second.Error?.ToString());

        sink.AppliedSequences.Should().Equal(5L, 6L);
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
        coordinatorResult.IsSuccess.Should().BeTrue(coordinatorResult.Error?.ToString());
        var coordinator = coordinatorResult.Value;

        var evt = CreateEvent(sequence: 10);

        var first = await coordinator.RecordAsync(evt, CancellationToken.None);
        var duplicate = await coordinator.RecordAsync(evt, CancellationToken.None); // duplicate should be ignored without throwing

        first.IsSuccess.Should().BeTrue(first.Error?.ToString());
        duplicate.IsSuccess.Should().BeTrue(duplicate.Error?.ToString());
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

    private sealed class DelegatingSink(Func<Result<Unit>> callback) : IResourceLeaseReplicationSink
    {
        public ValueTask<Result<Unit>> ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            var result = callback();
            return ValueTask.FromResult(result);
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
