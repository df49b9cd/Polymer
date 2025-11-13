using Npgsql;
using OmniRelay.ShardStore.Relational;

namespace OmniRelay.ShardStore.Postgres;

/// <summary>Factory helpers for building shard stores backed by PostgreSQL.</summary>
public static class PostgresShardStoreFactory
{
    public static RelationalShardStore Create(string connectionString, TimeProvider? timeProvider = null)
    {
        return new RelationalShardStore(() => new NpgsqlConnection(connectionString), timeProvider);
    }
}
