using Microsoft.Data.Sqlite;
using OmniRelay.ShardStore.Relational;

namespace OmniRelay.ShardStore.Sqlite;

/// <summary>Factory helpers for building in-memory SQLite shard stores.</summary>
public static class SqliteShardStoreFactory
{
    public static RelationalShardStore Create(string connectionString, TimeProvider? timeProvider = null)
    {
        return new RelationalShardStore(() => new SqliteConnection(connectionString), timeProvider);
    }

    public static RelationalShardStore CreateInMemory(TimeProvider? timeProvider = null)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        return new RelationalShardStore(() => new SqliteConnection(connectionString), timeProvider);
    }
}
