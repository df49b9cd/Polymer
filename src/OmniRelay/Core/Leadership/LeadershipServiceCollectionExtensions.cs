using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OmniRelay.Core.Leadership;

/// <summary>DI helpers for wiring the leadership coordinator.</summary>
public static class LeadershipServiceCollectionExtensions
{
    [UnconditionalSuppressMessage("TrimAnalysis", "IL2026", Justification = "Leadership options binding occurs in non-trimmed hosting environments.")]
    public static IServiceCollection AddLeadershipCoordinator(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LeadershipOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations();

        return services.AddLeadershipCoordinator();
    }

    public static IServiceCollection AddLeadershipCoordinator(this IServiceCollection services, Action<LeadershipOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<LeadershipOptions>, DefaultLeadershipOptions>());
        }

        services.TryAddSingleton<ILeadershipStore, InMemoryLeadershipStore>();
        services.TryAddSingleton<LeadershipEventHub>();
        services.TryAddSingleton<LeadershipCoordinator>();
        services.TryAddSingleton<ILeadershipObserver>(static sp => sp.GetRequiredService<LeadershipCoordinator>());

        return services;
    }

    private sealed class DefaultLeadershipOptions : IConfigureOptions<LeadershipOptions>
    {
        public void Configure(LeadershipOptions options)
        {
            // Intentionally left blank – provides defaults when no configuration section exists.
        }
    }
}
