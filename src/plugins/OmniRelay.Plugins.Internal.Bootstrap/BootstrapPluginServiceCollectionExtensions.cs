using Microsoft.Extensions.DependencyInjection;
using OmniRelay.ControlPlane.Bootstrap;

namespace OmniRelay.Plugins.Internal.Bootstrap;

public static class BootstrapPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalBootstrapPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<BootstrapTokenService>();
        services.AddSingleton<BootstrapServer>();
        services.AddSingleton<BootstrapClient>();
        services.AddSingleton<IBootstrapReplayProtector, InMemoryBootstrapReplayProtector>();
        return services;
    }
}
