using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Durable replication hub that persists events inside a SQLite database file before fanning out to sinks.
/// </summary>
public sealed class SqliteResourceLeaseReplicator : IResourceLeaseReplicator, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _tableName;
    private readonly ImmutableArray<IResourceLeaseReplicationSink> _sinks;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private long _sequenceNumber;
    private bool _initialized;

    public SqliteResourceLeaseReplicator(
        string connectionString,
        string tableName = "ResourceLeaseReplicationEvents",
        IEnumerable<IResourceLeaseReplicationSink>? sinks = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _tableName = tableName;
        _sinks = sinks is null
            ? ImmutableArray<IResourceLeaseReplicationSink>.Empty
            : sinks.Where(s => s is not null).ToImmutableArray();
    }

    public async ValueTask PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replicationEvent);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            Timestamp = DateTimeOffset.UtcNow
        };

        await PersistAsync(ordered, cancellationToken).ConfigureAwait(false);
        await FanOutAsync(ordered, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _initializationGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var create = connection.CreateCommand();
            create.CommandText = $@"
CREATE TABLE IF NOT EXISTS {_tableName} (
    sequence_number INTEGER PRIMARY KEY,
    event_type INTEGER NOT NULL,
    timestamp TEXT NOT NULL,
    peer_id TEXT NULL,
    event_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_{_tableName}_EventType ON {_tableName} (event_type);";
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using var maxCommand = connection.CreateCommand();
            maxCommand.CommandText = $"SELECT IFNULL(MAX(sequence_number), 0) FROM {_tableName};";
            var result = await maxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is long seq)
            {
                _sequenceNumber = seq;
            }
            else if (result is int seq32)
            {
                _sequenceNumber = seq32;
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task PersistAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var insert = connection.CreateCommand();
        insert.CommandText = $@"
INSERT INTO {_tableName} (sequence_number, event_type, timestamp, peer_id, event_json)
VALUES ($sequence, $type, $timestamp, $peerId, $json);";

        insert.Parameters.AddWithValue("$sequence", replicationEvent.SequenceNumber);
        insert.Parameters.AddWithValue("$type", (int)replicationEvent.EventType);
        insert.Parameters.AddWithValue("$timestamp", replicationEvent.Timestamp.ToString("O"));
        insert.Parameters.AddWithValue("$peerId", (object?)replicationEvent.PeerId ?? DBNull.Value);

        var json = JsonSerializer.Serialize(replicationEvent, ResourceLeaseJson.Options);
        insert.Parameters.AddWithValue("$json", json);

        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
