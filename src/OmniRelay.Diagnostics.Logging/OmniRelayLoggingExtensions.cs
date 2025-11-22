using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Diagnostics;

/// <summary>Extension helpers for configuring OmniRelay logging defaults.</summary>
public static class OmniRelayLoggingExtensions
{
    public static ILoggingBuilder AddOmniRelayLogging(this ILoggingBuilder builder, OmniRelayLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.Services.Configure<LoggerFilterOptions>(filterOptions =>
        {
            if (options.MinimumLevel.HasValue)
            {
                filterOptions.MinLevel = options.MinimumLevel.Value;
            }

            foreach (var (category, level) in options.CategoryLevels)
            {
                filterOptions.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: category, logLevel: level, filter: null));
            }
        });

        if (options.EnableConsoleLogger)
        {
            builder.AddSimpleConsole(consoleOptions =>
            {
                consoleOptions.SingleLine = options.UseSingleLineConsole;
                consoleOptions.TimestampFormat = options.TimestampFormat;
            });
        }

        return builder;
    }
}
