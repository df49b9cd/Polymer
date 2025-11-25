using System.Text.Json;
using AwesomeAssertions;
using Hugo;
using Xunit;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class ObjectStorageResourceLeaseReplicatorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask PublishAsync_WritesBlobAndNotifiesSinks()
    {
        using var temp = new TempDirectory();
        var store = new FileSystemResourceLeaseObjectStore(temp.Path);
        var sink = new RecordingSink();
        var replicator = new ObjectStorageResourceLeaseReplicator(store, sinks: [sink]);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await replicator.PublishAsync(CreateEvent(), cancellationToken);

        result.IsSuccess.Should().BeTrue(result.Error?.ToString());

        sink.Events.Should().ContainSingle();
        sink.Events[0].SequenceNumber.Should().Be(1);

        var keys = await store.ListKeysAsync("resourcelease/", cancellationToken);
        keys.Should().ContainSingle();

        var blobPath = Path.Combine(temp.Path, keys[0].Replace('/', Path.DirectorySeparatorChar));
        var json = await File.ReadAllTextAsync(blobPath, cancellationToken);
        var stored = JsonSerializer.Deserialize(json, ResourceLeaseJsonContext.Default.ResourceLeaseReplicationEvent);
        stored?.SequenceNumber.Should().Be(sink.Events[0].SequenceNumber);
    }

    private static ResourceLeaseReplicationEvent CreateEvent() =>
        new(
            0,
            ResourceLeaseReplicationEventType.Enqueue,
            DateTimeOffset.UtcNow,
            null,
            "peer",
            new ResourceLeaseItemPayload("type", "id", "partition", "json", [], null, "req"),
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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"resourcelease-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
