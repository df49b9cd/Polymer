using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polymer.Configuration.Internal;
using Polymer.Configuration.Models;

namespace Polymer.Configuration;

public static class PolymerServiceCollectionExtensions
{
    public static IServiceCollection AddPolymerDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(configuration);

        var snapshot = new PolymerConfigurationOptions();
        configuration.Bind(snapshot);
        ValidateBasicConfiguration(snapshot);

        var (minimumLevel, overrides) = ParseLoggingConfiguration(snapshot.Logging);

        services.Configure<PolymerConfigurationOptions>(configuration);

        if (minimumLevel.HasValue || overrides.Count > 0)
        {
            services.Configure<LoggerFilterOptions>(options =>
            {
                if (minimumLevel.HasValue)
                {
                    options.MinLevel = minimumLevel.Value;
                }

                foreach (var (category, level) in overrides)
                {
                    options.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: category, logLevel: level, filter: null));
                }
            });
        }

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PolymerConfigurationOptions>>().Value;
            var builder = new DispatcherBuilder(options, provider, configuration);
            return builder.Build();
        });

        services.AddSingleton<IHostedService>(provider =>
        {
            var dispatcher = provider.GetRequiredService<Dispatcher.Dispatcher>();
            var logger = provider.GetService<ILogger<DispatcherHostedService>>();
            return new DispatcherHostedService(dispatcher, logger);
        });

        return services;
    }

    private static void ValidateBasicConfiguration(PolymerConfigurationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Service))
        {
            throw new PolymerConfigurationException("Polymer configuration must specify a service name.");
        }
    }

    private static (LogLevel? Level, List<(string Category, LogLevel Level)> Overrides) ParseLoggingConfiguration(LoggingConfiguration logging)
    {
        LogLevel? minimumLevel = null;
        if (!string.IsNullOrWhiteSpace(logging.Level))
        {
            if (Enum.TryParse<LogLevel>(logging.Level, ignoreCase: true, out var parsed))
            {
                minimumLevel = parsed;
            }
            else
            {
                throw new PolymerConfigurationException($"Logging level '{logging.Level}' is not a valid value. Expected values match {nameof(LogLevel)}.");
            }
        }

        var overrides = new List<(string Category, LogLevel Level)>();
        foreach (var entry in logging.Overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            if (!Enum.TryParse<LogLevel>(entry.Value, ignoreCase: true, out var parsed))
            {
                throw new PolymerConfigurationException($"Logging override for '{entry.Key}' uses invalid level '{entry.Value}'.");
            }

            overrides.Add((entry.Key, parsed));
        }

        return (minimumLevel, overrides);
    }
}
