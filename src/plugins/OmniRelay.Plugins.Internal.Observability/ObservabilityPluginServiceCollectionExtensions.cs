using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniRelay.Diagnostics;
using OmniRelay.Diagnostics.Alerting;

namespace OmniRelay.Plugins.Internal.Observability;

/// <summary>Registers built-in observability components (telemetry, logging, probes, alerting, docs).</summary>
public static class ObservabilityPluginServiceCollectionExtensions
{
    public static IServiceCollection AddInternalObservabilityPlugins(this IServiceCollection services, OmniRelayTelemetryOptions? telemetryOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Telemetry (OTLP/Prometheus) registration
        services.AddOmniRelayTelemetry(telemetryOptions ?? new OmniRelayTelemetryOptions());

        // Logging defaults
        services.AddLogging(builder => builder.AddOmniRelayLogging(new OmniRelayLoggingOptions()));

        // Probes & chaos (developer diagnostics)
        services.AddOmniRelayProbes();

        // Alerting infrastructure (webhook channel only by default)
        services.AddSingleton<IAlertChannel>(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            return new WebhookAlertChannel("default-webhook", new Uri("http://localhost"), client, new Dictionary<string, string>(), null);
        });
        services.AddSingleton<IAlertPublisher>(sp =>
        {
            var channels = sp.GetServices<IAlertChannel>().ToList();
            return new AlertPublisher(channels, new Dictionary<string, TimeSpan>(), TimeSpan.FromMinutes(1), sp.GetRequiredService<ILogger<AlertPublisher>>());
        });

        // Documentation/metadata endpoints (no-op unless host maps them)
        return services;
    }
}
