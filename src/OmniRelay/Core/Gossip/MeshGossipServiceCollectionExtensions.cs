using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Core.Peers;

namespace OmniRelay.Core.Gossip;

/// <summary>DI helpers to register the mesh gossip agent.</summary>
public static class MeshGossipServiceCollectionExtensions
{
    [UnconditionalSuppressMessage("TrimAnalysis", "IL2026", Justification = "Mesh gossip options are bound from configuration in non-trimmed hosting scenarios.")]
    public static IServiceCollection AddMeshGossipAgent(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<MeshGossipOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations();
        return services.AddMeshGossipAgent();
    }

    public static IServiceCollection AddMeshGossipAgent(this IServiceCollection services, Action<MeshGossipOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<MeshGossipOptions>, DefaultMeshGossipOptions>());
        }

        services.AddSingleton<IMeshGossipAgent>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MeshGossipOptions>>().Value;
            if (!options.Enabled)
            {
                return NullMeshGossipAgent.Instance;
            }

            var logger = sp.GetRequiredService<ILogger<MeshGossipHost>>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var tracker = sp.GetService<PeerLeaseHealthTracker>();
            return new MeshGossipHost(options, metadata: null, logger, loggerFactory, timeProvider, tracker);
        });

        return services;
    }

    private sealed class DefaultMeshGossipOptions : IConfigureOptions<MeshGossipOptions>
    {
        public void Configure(MeshGossipOptions options)
        {
            // No-op placeholder so the options system can resolve a value even if not configured.
        }
    }
}
