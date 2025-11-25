using Microsoft.Extensions.DependencyInjection;
using OmniRelay.DataPlane.Core.Extensions;

namespace OmniRelay.Plugins.Internal.Extensions;

public static class ExtensionsPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalExtensionsPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<WasmExtensionHost>();
        services.AddSingleton<NativeExtensionHost>();
        services.AddSingleton<DslExtensionHost>();
        return services;
    }
}
