using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OmniRelay.ControlPlane.ControlProtocol;

public static class ControlProtocolServiceCollectionExtensions
{
    /// <summary>Adds the control-plane watch protocol components (update stream + gRPC service).</summary>
    public static IServiceCollection AddControlProtocol(this IServiceCollection services, Action<ControlProtocolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ControlProtocolOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ControlPlaneUpdateStream>();
        services.TryAddSingleton<IControlPlaneUpdateSource>(sp => sp.GetRequiredService<ControlPlaneUpdateStream>());
        services.TryAddSingleton<IControlPlaneUpdatePublisher>(sp => sp.GetRequiredService<ControlPlaneUpdateStream>());
        services.TryAddSingleton<ControlPlaneWatchService>();

        return services;
    }
}
