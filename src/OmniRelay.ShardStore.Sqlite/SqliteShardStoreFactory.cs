using Microsoft.Data.Sqlite;
using OmniRelay.ShardStore.Relational;

namespace OmniRelay.ShardStore.Sqlite;

/// <summary>Factory helpers for building shard stores backed by SQLite.</summary>
public static class SqliteShardStoreFactory
{
    public static RelationalShardStore CreateInMemory(string databaseName = "omnirelay-shards", TimeProvider? timeProvider = null)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseName,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        return Create(connectionString, timeProvider);
    }

    public static RelationalShardStore Create(string connectionString, TimeProvider? timeProvider = null)
    {
        return new RelationalShardStore(() => new SqliteConnection(connectionString), timeProvider);
    }
}
