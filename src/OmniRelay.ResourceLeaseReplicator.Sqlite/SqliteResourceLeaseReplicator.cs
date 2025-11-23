using System.Collections.Immutable;
using System.Text.Json;
using Hugo;
using Microsoft.Data.Sqlite;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

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
            ? []
            : [.. sinks.Where(s => s is not null)];
    }

    public async ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        if (replicationEvent is null)
        {
            return Err<Unit>(ResourceLeaseReplicationErrors.EventRequired("sqlite.publish"));
        }

        var initialized = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (initialized.IsFailure)
        {
            return initialized;
        }

        var ordered = replicationEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            Timestamp = DateTimeOffset.UtcNow
        };

        var persisted = await PersistAsync(ordered, cancellationToken).ConfigureAwait(false);
        if (persisted.IsFailure)
        {
            return persisted;
        }

        return await FanOutAsync(ordered, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _initializationGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<Result<Unit>> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return Ok(Unit.Value);
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return Ok(Unit.Value);
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
            return Ok(Unit.Value);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<Unit>(Error.Canceled("sqlite replication initialization canceled", cancellationToken)
                .WithMetadata("replication.stage", "sqlite.initialize")
                .WithMetadata("replication.table", _tableName));
        }
        catch (Exception ex)
        {
            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", "sqlite.initialize")
                .WithMetadata("replication.table", _tableName));
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async ValueTask<Result<Unit>> PersistAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        try
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

            var json = JsonSerializer.Serialize(replicationEvent, ResourceLeaseJsonContext.Default.ResourceLeaseReplicationEvent);
            insert.Parameters.AddWithValue("$json", json);

            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Ok(Unit.Value);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<Unit>(Error.Canceled("sqlite replication persist canceled", cancellationToken)
                .WithMetadata("replication.stage", "sqlite.persist")
                .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                .WithMetadata("replication.table", _tableName));
        }
        catch (Exception ex)
        {
            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", "sqlite.persist")
                .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                .WithMetadata("replication.table", _tableName));
        }
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
                        .WithMetadata("replication.stage", "sqlite.sink")
                        .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                        .WithMetadata("replication.sink", sink.GetType().Name));
                }
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
            {
                return Err<Unit>(Error.Canceled("sqlite sink canceled", cancellationToken)
                    .WithMetadata("replication.stage", "sqlite.sink")
                    .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                    .WithMetadata("replication.sink", sink.GetType().Name));
            }
            catch (Exception ex)
            {
                return Err<Unit>(Error.FromException(ex)
                    .WithMetadata("replication.stage", "sqlite.sink")
                    .WithMetadata("replication.sequence", replicationEvent.SequenceNumber)
                    .WithMetadata("replication.sink", sink.GetType().Name));
            }
        }

        return Ok(Unit.Value);
    }
}
