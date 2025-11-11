using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class ResourceLeaseReplicationTests
{
    [Fact]
    public async Task InMemoryReplicator_SequencesAndDeliversEvents()
    {
        var sink = new RecordingSink();
        var replicator = new InMemoryResourceLeaseReplicator([sink]);

        await replicator.PublishAsync(CreateEvent(), CancellationToken.None);
        await replicator.PublishAsync(CreateEvent(), CancellationToken.None);

        Assert.Equal([1L, 2L], sink.Events.Select(evt => evt.SequenceNumber).ToArray());
    }

    [Fact]
    public async Task InMemoryReplicator_IgnoresProvidedSequenceAndUsesStartingOffset()
    {
        var sink = new RecordingSink();
        var replicator = new InMemoryResourceLeaseReplicator([sink], startingSequence: 10);

        await replicator.PublishAsync(CreateEvent(sequence: 42), CancellationToken.None);

        Assert.Single(sink.Events);
        Assert.Equal(11L, sink.Events[0].SequenceNumber);
    }

    [Fact]
    public async Task CheckpointingSink_DeduplicatesSequences()
    {
        var sink = new CountingCheckpointSink();

        await sink.ApplyAsync(CreateEvent(sequence: 5), CancellationToken.None);
        await sink.ApplyAsync(CreateEvent(sequence: 5), CancellationToken.None);
        await sink.ApplyAsync(CreateEvent(sequence: 6), CancellationToken.None);

        Assert.Equal(new[] { 5L, 6L }, sink.AppliedSequences);
    }

    [Fact]
    public async Task DeterministicCoordinator_IgnoresDuplicateEffects()
    {
        var coordinator = new DeterministicResourceLeaseCoordinator(new ResourceLeaseDeterministicOptions
        {
            ChangeId = "leases",
            MinVersion = 1,
            MaxVersion = 1,
            StateStore = new InMemoryDeterministicStateStore()
        });

        var evt = CreateEvent(sequence: 10);

        await coordinator.RecordAsync(evt, CancellationToken.None);
        await coordinator.RecordAsync(evt, CancellationToken.None); // duplicate should be ignored without throwing
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
            ImmutableDictionary<string, string>.Empty);

    private sealed class RecordingSink : IResourceLeaseReplicationSink
    {
        public List<ResourceLeaseReplicationEvent> Events { get; } = [];

        public ValueTask ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            Events.Add(replicationEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingCheckpointSink : CheckpointingResourceLeaseReplicationSink
    {
        public List<long> AppliedSequences { get; } = [];

        protected override ValueTask ApplyInternalAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            AppliedSequences.Add(replicationEvent.SequenceNumber);
            return ValueTask.CompletedTask;
        }
    }
}
