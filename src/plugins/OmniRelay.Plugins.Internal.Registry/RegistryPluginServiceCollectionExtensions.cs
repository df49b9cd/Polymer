using Microsoft.Extensions.DependencyInjection;
using OmniRelay.ShardStore.ObjectStorage;
using OmniRelay.ShardStore.Postgres;
using OmniRelay.ShardStore.Relational;
using OmniRelay.ShardStore.Sqlite;

namespace OmniRelay.Plugins.Internal.Registry;

/// <summary>Registers built-in shard registry stores.</summary>
public static class RegistryPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalRegistryPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register factory helpers; concrete store creation remains caller-configured.
        services.AddSingleton(() => new Func<string, TimeProvider?, OmniRelay.ShardStore.Relational.RelationalShardStore>(PostgresShardStoreFactory.Create));
        services.AddSingleton(() => new Func<TimeProvider?, OmniRelay.ShardStore.Relational.RelationalShardStore>(SqliteShardStoreFactory.CreateInMemory));
        services.AddSingleton<ObjectStorageShardStore>();

        // Relational shard store can be constructed with a DbConnection factory at runtime.
        services.AddTransient<RelationalShardStore>(sp => new RelationalShardStore(() => throw new InvalidOperationException("Provide DbConnection factory")));

        return services;
    }
}
