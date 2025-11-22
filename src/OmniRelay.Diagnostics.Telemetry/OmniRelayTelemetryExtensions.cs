using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OmniRelay.Diagnostics;

/// <summary>ServiceCollection helpers for configuring OmniRelay telemetry exporters.</summary>
public static class OmniRelayTelemetryExtensions
{
    private static readonly string[] OmniRelayMeters =
    [
        "OmniRelay.Core.Peers",
        "OmniRelay.Core.Gossip",
        "OmniRelay.Core.Leadership",
        "OmniRelay.Transport.Grpc",
        "OmniRelay.Transport.Http",
        "OmniRelay.Rpc",
        "Hugo.Go"
    ];

    public static IServiceCollection AddOmniRelayTelemetry(
        this IServiceCollection services,
        OmniRelayTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.EnableTelemetry)
        {
            return services;
        }

        var openTelemetryBuilder = services.AddOpenTelemetry();
        openTelemetryBuilder.ConfigureResource(resource => resource.AddService(serviceName: options.ServiceName));

        if (options.EnableMetrics)
        {
            openTelemetryBuilder.WithMetrics(builder =>
            {
                builder.AddMeter(OmniRelayMeters);

                if (options.Prometheus.Enabled)
                {
                    builder.AddPrometheusExporter(prometheusOptions =>
                    {
                        prometheusOptions.ScrapeEndpointPath = NormalizeScrapeEndpointPath(options.Prometheus.ScrapeEndpointPath);
                    });
                }

                if (options.Otlp.Enabled)
                {
                    builder.AddOtlpExporter(otlpOptions =>
                    {
                        otlpOptions.Protocol = ParseOtlpProtocol(options.Otlp.Protocol);
                        if (Uri.TryCreate(options.Otlp.Endpoint, UriKind.Absolute, out var endpoint))
                        {
                            otlpOptions.Endpoint = endpoint;
                        }
                    });
                }
            });

            services.AddHostedService<DiagnosticsRegistrationHostedService>();
        }

        if (options.EnableTracing)
        {
            openTelemetryBuilder.WithTracing(builder =>
            {
                builder.AddSource("OmniRelay.Rpc", "OmniRelay.Transport.Grpc");
                if (options.EnableRuntimeTraceSampler)
                {
                    builder.SetSampler(provider =>
                    {
                        var runtime = provider.GetService<IDiagnosticsRuntime>();
                        return new DiagnosticsRuntimeSampler(runtime, new AlwaysOnSampler());
                    });
                }

                if (options.Otlp.Enabled)
                {
                    builder.AddOtlpExporter(otlpOptions =>
                    {
                        otlpOptions.Protocol = ParseOtlpProtocol(options.Otlp.Protocol);
                        if (Uri.TryCreate(options.Otlp.Endpoint, UriKind.Absolute, out var endpoint))
                        {
                            otlpOptions.Endpoint = endpoint;
                        }
                    });
                }
            });
        }

        return services;
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

        throw new InvalidOperationException(
            $"OTLP protocol '{protocol}' is not valid. Supported values: {string.Join(", ", Enum.GetNames<OtlpExportProtocol>())}.");
    }
}
