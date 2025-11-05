using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using OmniRelay.Configuration.Internal;
using OmniRelay.Configuration.Models;
using OmniRelay.Core.Diagnostics;

namespace OmniRelay.Configuration;

public static class OmniRelayServiceCollectionExtensions
{
    public static IServiceCollection AddOmniRelayDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(configuration);

        var snapshot = new OmniRelayConfigurationOptions();
        configuration.Bind(snapshot);
        ValidateBasicConfiguration(snapshot);

        var (minimumLevel, overrides) = ParseLoggingConfiguration(snapshot.Logging);

        services.Configure<OmniRelayConfigurationOptions>(configuration);

    // Ensure HttpClientFactory is available so named HTTP outbounds can be used if configured.
    services.AddHttpClient();

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
            var options = provider.GetRequiredService<IOptions<OmniRelayConfigurationOptions>>().Value;
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

    private static void ConfigureDiagnostics(IServiceCollection services, OmniRelayConfigurationOptions options)
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

        var serviceName = string.IsNullOrWhiteSpace(otel.ServiceName) ? options.Service ?? "OmniRelay" : otel.ServiceName!;

        var openTelemetryBuilder = services.AddOpenTelemetry();
        openTelemetryBuilder.ConfigureResource(resource => resource.AddService(serviceName: serviceName));

        if (metricsEnabled)
        {
            openTelemetryBuilder.WithMetrics(builder =>
            {
                builder.AddMeter("OmniRelay.Core.Peers", "OmniRelay.Transport.Grpc", "OmniRelay.Rpc", "Hugo.Go");

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
                                throw new OmniRelayConfigurationException($"OTLP endpoint '{otel.Otlp.Endpoint}' is not a valid absolute URI.");
                            }

                            options.Endpoint = endpoint;
                        }
                    });
                }
            });

            services.AddHostedService<DiagnosticsRegistrationHostedService>();
        }

        // Enable tracing pipeline if explicitly enabled in configuration (primarily for OTLP export).
        var tracingEnabled = otelEnabled && (otlpEnabled || (diagnostics.Runtime?.EnableTraceSamplingToggle ?? false));
        if (tracingEnabled)
        {
            openTelemetryBuilder.WithTracing(builder =>
            {
                builder.AddSource("OmniRelay.Rpc", "OmniRelay.Transport.Grpc");

                if (otlpEnabled)
                {
                    builder.AddOtlpExporter(options =>
                    {
                        options.Protocol = ParseOtlpProtocol(otel.Otlp.Protocol);
                        if (!string.IsNullOrWhiteSpace(otel.Otlp.Endpoint))
                        {
                            if (!Uri.TryCreate(otel.Otlp.Endpoint, UriKind.Absolute, out var endpoint))
                            {
                                throw new OmniRelayConfigurationException($"OTLP endpoint '{otel.Otlp.Endpoint}' is not a valid absolute URI.");
                            }

                            options.Endpoint = endpoint;
                        }
                    });
                }
            });
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
            return "/omnirelay/metrics";
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

        throw new OmniRelayConfigurationException(
            $"OTLP protocol '{protocol}' is not valid. Supported values: {string.Join(", ", Enum.GetNames(typeof(OtlpExportProtocol)))}.");
    }

    private static void ValidateBasicConfiguration(OmniRelayConfigurationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Service))
        {
            throw new OmniRelayConfigurationException("OmniRelay configuration must specify a service name.");
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
                throw new OmniRelayConfigurationException($"Logging level '{logging.Level}' is not a valid value. Expected values match {nameof(LogLevel)}.");
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
                throw new OmniRelayConfigurationException($"Logging override for '{entry.Key}' uses invalid level '{entry.Value}'.");
            }

            overrides.Add((entry.Key, parsed));
        }

        return (minimumLevel, overrides);
    }
}
