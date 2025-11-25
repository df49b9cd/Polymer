using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Identity;
using OmniRelay.Core.Leadership;

namespace OmniRelay.ControlPlane.Agent;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddMeshAgent(this IServiceCollection services, Func<IServiceProvider, IControlPlaneWatchClient> clientFactory, string lkgPath)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return services.AddMeshAgent(clientFactory, options => options.LkgPath = lkgPath);
    }

    public static IServiceCollection AddMeshAgent(
        this IServiceCollection services,
        Func<IServiceProvider, IControlPlaneWatchClient> clientFactory,
        Action<MeshAgentOptions>? configure = null,
        Func<IServiceProvider, ICertificateAuthorityClient>? caClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);

        services.AddOptions<MeshAgentOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.PostConfigure<MeshAgentOptions>(options =>
        {
            options.NodeId ??= Environment.MachineName;
            options.LkgPath ??= Path.Combine(AppContext.BaseDirectory, "lkg", "control.json");

            if (options.Capabilities.Count == 0)
            {
                options.Capabilities.Add("core/v1");
                options.Capabilities.Add("dsl/v1");
            }

            options.LkgCache ??= new LkgCacheOptions();
            if (options.LkgCache.SigningKey is null || options.LkgCache.SigningKey.Length == 0)
            {
                options.LkgCache.SigningKey = Encoding.UTF8.GetBytes(options.NodeId ?? string.Empty);
            }

            options.LkgCache.RequireSignature = options.LkgCache.RequireSignature || (options.LkgCache.SigningKey?.Length > 0);
        });

        // Agents should never participate in leader elections.
        services.PostConfigure<LeadershipOptions>(options => options.Enabled = false);

        services.TryAddSingleton(clientFactory);
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MeshAgentOptions>>().Value;
            return new LkgCache(opts.LkgPath, opts.LkgCache);
        });
        services.TryAddSingleton<TelemetryForwarder>();
        services.TryAddSingleton<IControlPlaneConfigValidator, DefaultConfigValidator>();
        services.TryAddSingleton<IControlPlaneConfigApplier, NullConfigApplier>();
        services.TryAddSingleton<WatchHarness>();

        if (caClientFactory is not null)
        {
            services.TryAddSingleton(caClientFactory);
            services.TryAddSingleton<AgentCertificateManager>();
        }

        services.AddSingleton<MeshAgent>();
        services.AddSingleton<IHostedService, MeshAgentHostedService>();
        return services;
    }
}
