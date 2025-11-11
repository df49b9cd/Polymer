using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class SqliteResourceLeaseReplicatorTests
{
    [Fact]
    public async Task PublishAsync_PersistsEventAndNotifiesSinks()
    {
        using var temp = new TempFile();
        var connectionString = $"Data Source={temp.Path}";
        var sink = new RecordingSink();
        var replicator = new SqliteResourceLeaseReplicator(connectionString, sinks: [sink]);

        var evt = CreateEvent();
        var cancellationToken = TestContext.Current.CancellationToken;
        await replicator.PublishAsync(evt, cancellationToken);

        Assert.Single(sink.Events);
        Assert.Equal(1, sink.Events[0].SequenceNumber);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_json FROM ResourceLeaseReplicationEvents LIMIT 1;";
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        Assert.NotNull(json);

        var stored = JsonSerializer.Deserialize(json!, ResourceLeaseJsonContext.Default.ResourceLeaseReplicationEvent);
        Assert.NotNull(stored);
        Assert.Equal(sink.Events[0].SequenceNumber, stored!.SequenceNumber);
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

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        }

        public string Path { get; }

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
