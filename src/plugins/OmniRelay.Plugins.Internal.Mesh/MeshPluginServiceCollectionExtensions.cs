using Microsoft.Extensions.DependencyInjection;
using OmniRelay.ControlPlane.Core.Gossip;
using OmniRelay.ControlPlane.Core;

namespace OmniRelay.Plugins.Internal.Mesh;

public static class MeshPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalMeshPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<MeshGossipHost>();
        services.AddSingleton<LeadershipCoordinator>();
        return services;
    }
}
