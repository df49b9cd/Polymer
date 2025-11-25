using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Dispatcher;

namespace OmniRelay.Plugins.Internal.Replication;

public static class ReplicationPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalReplicationPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SqliteResourceLeaseReplicator>();
        services.AddSingleton<ObjectStorageResourceLeaseReplicator>();
        services.AddSingleton<GrpcResourceLeaseReplicator>();
        return services;
    }
}
