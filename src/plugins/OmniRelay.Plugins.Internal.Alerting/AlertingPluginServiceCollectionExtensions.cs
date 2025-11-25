using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Diagnostics.Alerting;

namespace OmniRelay.Plugins.Internal.Alerting;

public static class AlertingPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalAlertingPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAlertChannel, NullAlertChannel>();
        services.AddSingleton<IAlertPublisher, NullAlertPublisher>();
        services.AddSingleton<AlertThrottler>();
        return services;
    }
}
