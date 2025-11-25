using Microsoft.Extensions.DependencyInjection;
using OmniRelay.ControlPlane.Shards.Hashing;

namespace OmniRelay.Plugins.Internal.Topology;

public static class TopologyPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalTopologyPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ShardHashStrategyRegistry>();
        services.AddSingleton<RendezvousShardHashStrategy>();
        services.AddSingleton<RingShardHashStrategy>();
        services.AddSingleton<LocalityAwareShardHashStrategy>();
        return services;
    }
}
