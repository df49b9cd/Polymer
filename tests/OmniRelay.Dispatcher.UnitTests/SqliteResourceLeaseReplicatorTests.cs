using System.Text.Json;
using AwesomeAssertions;
using Hugo;
using Microsoft.Data.Sqlite;
using Xunit;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class SqliteResourceLeaseReplicatorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask PublishAsync_PersistsEventAndNotifiesSinks()
    {
        using var temp = new TempFile();
        var connectionString = $"Data Source={temp.Path}";
        var sink = new RecordingSink();
        var replicator = new SqliteResourceLeaseReplicator(connectionString, sinks: [sink]);

        var evt = CreateEvent();
        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await replicator.PublishAsync(evt, cancellationToken);

        result.IsSuccess.Should().BeTrue(result.Error?.ToString());

        sink.Events.Should().ContainSingle();
        sink.Events[0].SequenceNumber.Should().Be(1);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_json FROM ResourceLeaseReplicationEvents LIMIT 1;";
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        json.Should().NotBeNull();

        var stored = JsonSerializer.Deserialize(json!, ResourceLeaseJsonContext.Default.ResourceLeaseReplicationEvent);
        stored.Should().NotBeNull();
        stored!.SequenceNumber.Should().Be(sink.Events[0].SequenceNumber);
    }

    private static ResourceLeaseReplicationEvent CreateEvent() =>
        new(
            0,
            ResourceLeaseReplicationEventType.Enqueue,
            DateTimeOffset.UtcNow,
            null,
            "peer",
            new ResourceLeaseItemPayload("type", "id", "pk", "json", [], new Dictionary<string, string> { ["owner"] = "test" }),
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

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            if (!File.Exists(Path))
            {
                return;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.Delete(Path);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
