using System.Globalization;
using Hugo;
using Microsoft.Data.Sqlite;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Production-ready <see cref="IDeterministicStateStore"/> built on SQLite. Suitable for single-node deployments or integration tests.
/// </summary>
public sealed class SqliteDeterministicStateStore : IDeterministicStateStore
{
    private readonly string _connectionString;
    private readonly string _tableName;
    private readonly object _initializationLock = new();
    private bool _initialized;

    public SqliteDeterministicStateStore(string connectionString, string tableName = "DeterministicStateStore")
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _tableName = tableName;
    }

    public bool TryGet(string key, out DeterministicRecord record)
    {
        EnsureInitialized();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT kind, version, recorded_at, payload FROM {_tableName} WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            record = null!;
            return false;
        }

        var kind = reader.GetString(0);
        var version = reader.GetInt32(1);
        var recordedAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var payload = (byte[])reader[3];

        record = new DeterministicRecord(kind, version, payload, recordedAt);
        return true;
    }

    public void Set(string key, DeterministicRecord record)
    {
        EnsureInitialized();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $@"
INSERT INTO {_tableName} (key, kind, version, recorded_at, payload)
VALUES ($key, $kind, $version, $recordedAt, $payload)
ON CONFLICT(key) DO UPDATE SET
    kind = excluded.kind,
    version = excluded.version,
    recorded_at = excluded.recorded_at,
    payload = excluded.payload;";

        BindRecordParameters(command, key, record);
        command.ExecuteNonQuery();
    }

    public bool TryAdd(string key, DeterministicRecord record)
    {
        EnsureInitialized();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $@"
INSERT INTO {_tableName} (key, kind, version, recorded_at, payload)
VALUES ($key, $kind, $version, $recordedAt, $payload)
ON CONFLICT(key) DO NOTHING;";

        BindRecordParameters(command, key, record);
        var affected = command.ExecuteNonQuery();
        return affected > 0;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_initialized)
            {
                return;
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $@"
CREATE TABLE IF NOT EXISTS {_tableName} (
    key TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    version INTEGER NOT NULL,
    recorded_at TEXT NOT NULL,
    payload BLOB NOT NULL
);";
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    private static void BindRecordParameters(SqliteCommand command, string key, DeterministicRecord record)
    {
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$kind", record.Kind);
        command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$recordedAt", record.RecordedAt.ToString("O"));
        command.Parameters.AddWithValue("$payload", record.Payload.ToArray());
    }
}
