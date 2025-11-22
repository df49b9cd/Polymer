using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OmniRelay.ControlPlane.ControlProtocol;

namespace OmniRelay.ControlPlane.Agent;

public static class MeshAgentServiceCollectionExtensions
{
    public static IServiceCollection AddMeshAgent(this IServiceCollection services, Func<IServiceProvider, IControlPlaneWatchClient> clientFactory, string lkgPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);
        if (string.IsNullOrWhiteSpace(lkgPath)) throw new ArgumentException("LKG path required", nameof(lkgPath));

        services.TryAddSingleton(clientFactory);
        services.TryAddSingleton(new LkgCache(lkgPath));
        services.TryAddSingleton<TelemetryForwarder>();
        services.AddSingleton<MeshAgent>();
        services.AddSingleton<IHostedService, MeshAgentHostedService>();
        return services;
    }
}
