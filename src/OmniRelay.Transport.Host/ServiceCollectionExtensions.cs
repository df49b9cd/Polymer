using Microsoft.Extensions.DependencyInjection;

namespace OmniRelay.Transport.Host;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data-plane host with DI. Assumes callers add concrete transports separately.
    /// </summary>
    public static IServiceCollection AddDataPlaneHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<DataPlaneHost>();
        return services;
    }
}
