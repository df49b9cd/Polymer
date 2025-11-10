using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class TableLeaseReplicationTests
{
    [Fact]
    public async Task InMemoryReplicator_SequencesAndDeliversEvents()
    {
        var sink = new RecordingSink();
        var replicator = new InMemoryTableLeaseReplicator(new[] { sink });

        await replicator.PublishAsync(CreateEvent(), CancellationToken.None);
        await replicator.PublishAsync(CreateEvent(), CancellationToken.None);

        Assert.Equal(new[] { 1L, 2L }, sink.Events.Select(evt => evt.SequenceNumber).ToArray());
    }

    [Fact]
    public async Task InMemoryReplicator_IgnoresProvidedSequenceAndUsesStartingOffset()
    {
        var sink = new RecordingSink();
        var replicator = new InMemoryTableLeaseReplicator(new[] { sink }, startingSequence: 10);

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
        var coordinator = new DeterministicTableLeaseCoordinator(new TableLeaseDeterministicOptions
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

    private static TableLeaseReplicationEvent CreateEvent(long sequence = 0) =>
        new(
            sequence,
            TableLeaseReplicationEventType.LeaseGranted,
            DateTimeOffset.UtcNow,
            new TableLeaseOwnershipHandle(5, 1, Guid.NewGuid()),
            "peer-a",
            new TableLeaseItemPayload("ns", "table", "pk", "json", Array.Empty<byte>()),
            null,
            ImmutableDictionary<string, string>.Empty);

    private sealed class RecordingSink : ITableLeaseReplicationSink
    {
        public List<TableLeaseReplicationEvent> Events { get; } = new();

        public ValueTask ApplyAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            Events.Add(replicationEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingCheckpointSink : CheckpointingTableLeaseReplicationSink
    {
        public List<long> AppliedSequences { get; } = new();

        protected override ValueTask ApplyInternalAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            AppliedSequences.Add(replicationEvent.SequenceNumber);
            return ValueTask.CompletedTask;
        }
    }
}
