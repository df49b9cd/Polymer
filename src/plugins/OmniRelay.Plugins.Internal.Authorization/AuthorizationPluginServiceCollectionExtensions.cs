using Microsoft.Extensions.DependencyInjection;
using OmniRelay.ControlPlane.Bootstrap;
using OmniRelay.ControlPlane.Security;
using OmniRelay.Security.Authorization;

namespace OmniRelay.Plugins.Internal.Authorization;

public static class AuthorizationPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalAuthorizationPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IMeshAuthorizationEvaluator, MeshAuthorizationEvaluator>();
        services.AddSingleton<MeshAuthorizationGrpcInterceptor>();
        services.AddSingleton<TransportSecurityPolicyEvaluator>();
        services.AddSingleton<BootstrapPolicyEvaluator>();
        return services;
    }
}
