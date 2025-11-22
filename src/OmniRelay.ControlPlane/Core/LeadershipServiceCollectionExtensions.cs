using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OmniRelay.Core.Leadership;

/// <summary>DI helpers for wiring the leadership coordinator.</summary>
public static class LeadershipServiceCollectionExtensions
{
    public static IServiceCollection AddLeadershipCoordinator(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LeadershipOptions>()
            .Configure(options => BindLeadershipOptions(configuration, options));

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
        services.TryAddSingleton<LeadershipControlGrpcService>();

        return services;
    }

    private sealed class DefaultLeadershipOptions : IConfigureOptions<LeadershipOptions>
    {
        public void Configure(LeadershipOptions options)
        {
            // Intentionally left blank – provides defaults when no configuration section exists.
        }
    }

    private static void BindLeadershipOptions(IConfiguration configuration, LeadershipOptions options)
    {
        options.Enabled = ReadBool(configuration, nameof(LeadershipOptions.Enabled)) ?? options.Enabled;
        options.LeaseDuration = ReadTimeSpan(configuration, nameof(LeadershipOptions.LeaseDuration)) ?? options.LeaseDuration;
        options.RenewalLeadTime = ReadTimeSpan(configuration, nameof(LeadershipOptions.RenewalLeadTime)) ?? options.RenewalLeadTime;
        options.EvaluationInterval = ReadTimeSpan(configuration, nameof(LeadershipOptions.EvaluationInterval)) ?? options.EvaluationInterval;
        options.MaxElectionWindow = ReadTimeSpan(configuration, nameof(LeadershipOptions.MaxElectionWindow)) ?? options.MaxElectionWindow;
        options.ElectionBackoff = ReadTimeSpan(configuration, nameof(LeadershipOptions.ElectionBackoff)) ?? options.ElectionBackoff;
        options.NodeId = configuration[nameof(LeadershipOptions.NodeId)] ?? options.NodeId;

        options.Scopes.Clear();
        foreach (var scopeSection in configuration.GetSection(nameof(LeadershipOptions.Scopes)).GetChildren())
        {
            var descriptor = new LeadershipScopeDescriptor
            {
                ScopeId = scopeSection[nameof(LeadershipScopeDescriptor.ScopeId)],
                Kind = scopeSection[nameof(LeadershipScopeDescriptor.Kind)],
                Namespace = scopeSection[nameof(LeadershipScopeDescriptor.Namespace)],
                ShardId = scopeSection[nameof(LeadershipScopeDescriptor.ShardId)],
                Labels = scopeSection.GetSection(nameof(LeadershipScopeDescriptor.Labels))
                    .GetChildren()
                    .ToDictionary(child => child.Key, child => child.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            };
            options.Scopes.Add(descriptor);
        }

        options.Shards.Clear();
        foreach (var shardSection in configuration.GetSection(nameof(LeadershipOptions.Shards)).GetChildren())
        {
            var shardOptions = new LeadershipShardScopeOptions
            {
                Namespace = shardSection[nameof(LeadershipShardScopeOptions.Namespace)]
            };

            foreach (var shardId in shardSection.GetSection(nameof(LeadershipShardScopeOptions.Shards)).GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(shardId.Value))
                {
                    shardOptions.Shards.Add(shardId.Value.Trim());
                }
            }

            options.Shards.Add(shardOptions);
        }
    }

    private static bool? ReadBool(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static TimeSpan? ReadTimeSpan(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
