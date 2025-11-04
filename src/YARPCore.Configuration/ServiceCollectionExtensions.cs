using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using YARPCore.Configuration.Internal;
using YARPCore.Configuration.Models;
using YARPCore.Core.Diagnostics;

namespace YARPCore.Configuration;

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

        ConfigureDiagnostics(services, snapshot);

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

        services.AddSingleton(provider => provider.GetRequiredService<Dispatcher.Dispatcher>().Codecs);

        services.AddSingleton<IHostedService>(provider =>
        {
            var dispatcher = provider.GetRequiredService<Dispatcher.Dispatcher>();
            var logger = provider.GetService<ILogger<DispatcherHostedService>>();
            return new DispatcherHostedService(dispatcher, logger);
        });

        return services;
    }

    private static void ConfigureDiagnostics(IServiceCollection services, PolymerConfigurationOptions options)
    {
        var diagnostics = options.Diagnostics;
        if (diagnostics is null)
        {
            return;
        }

        ConfigureRuntimeDiagnostics(services, diagnostics);

        var otel = diagnostics.OpenTelemetry ?? new OpenTelemetryConfiguration();

        var prometheusEnabled = otel.Prometheus.Enabled ?? true;
        var otlpEnabled = otel.Otlp.Enabled ?? false;
        var metricsEnabled = otel.EnableMetrics ?? (prometheusEnabled || otlpEnabled);

        if (!metricsEnabled)
        {
            prometheusEnabled = false;
            otlpEnabled = false;
        }

        var otelEnabled = otel.Enabled ?? metricsEnabled;
        if (!otelEnabled)
        {
            return;
        }

        var serviceName = string.IsNullOrWhiteSpace(otel.ServiceName) ? options.Service ?? "YARPCore" : otel.ServiceName!;

        var openTelemetryBuilder = services.AddOpenTelemetry();
        openTelemetryBuilder.ConfigureResource(resource => resource.AddService(serviceName: serviceName));

        if (metricsEnabled)
        {
            openTelemetryBuilder.WithMetrics(builder =>
            {
                builder.AddMeter("YARPCore.Core.Peers", "YARPCore.Transport.Grpc", "YARPCore.Rpc", "Hugo.Go");

                if (prometheusEnabled)
                {
                    builder.AddPrometheusExporter(options =>
                    {
                        options.ScrapeEndpointPath = NormalizeScrapeEndpointPath(otel.Prometheus.ScrapeEndpointPath);
                    });
                }

                if (otlpEnabled)
                {
                    builder.AddOtlpExporter(options =>
                    {
                        options.Protocol = ParseOtlpProtocol(otel.Otlp.Protocol);
                        if (!string.IsNullOrWhiteSpace(otel.Otlp.Endpoint))
                        {
                            if (!Uri.TryCreate(otel.Otlp.Endpoint, UriKind.Absolute, out var endpoint))
                            {
                                throw new PolymerConfigurationException($"OTLP endpoint '{otel.Otlp.Endpoint}' is not a valid absolute URI.");
                            }

                            options.Endpoint = endpoint;
                        }
                    });
                }
            });

            services.AddHostedService<DiagnosticsRegistrationHostedService>();
        }
    }

    private static void ConfigureRuntimeDiagnostics(
        IServiceCollection services,
        DiagnosticsConfiguration diagnostics)
    {
        var runtime = diagnostics.Runtime;
        if (runtime is null)
        {
            return;
        }

        var enableLoggingToggle = runtime.EnableLoggingLevelToggle ?? false;
        var enableSamplingToggle = runtime.EnableTraceSamplingToggle ?? false;
        var enableControlPlane = runtime.EnableControlPlane ?? (enableLoggingToggle || enableSamplingToggle);

        if (!enableControlPlane && !enableLoggingToggle && !enableSamplingToggle)
        {
            return;
        }

        services.TryAddSingleton<DiagnosticsRuntimeState>();
        services.TryAddSingleton<IDiagnosticsRuntime>(sp => sp.GetRequiredService<DiagnosticsRuntimeState>());
    }

    private static string NormalizeScrapeEndpointPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/polymer/metrics";
        }

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    private static OtlpExportProtocol ParseOtlpProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return OtlpExportProtocol.Grpc;
        }

        if (Enum.TryParse<OtlpExportProtocol>(protocol, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new PolymerConfigurationException(
            $"OTLP protocol '{protocol}' is not valid. Supported values: {string.Join(", ", Enum.GetNames(typeof(OtlpExportProtocol)))}.");
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
