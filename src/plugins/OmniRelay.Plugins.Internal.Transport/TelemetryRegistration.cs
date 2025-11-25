using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Diagnostics;

namespace OmniRelay.Plugins.Internal.Transport;

/// <summary>Telemetry registration helpers for transport plugin.</summary>
internal static class TelemetryRegistration
{
    public static void AddHttpTelemetry(this IServiceCollection services)
    {
        var options = new OmniRelayTelemetryOptions
        {
            EnableTelemetry = true,
            EnableMetrics = true,
            EnableTracing = true
        };
        options.Prometheus.Enabled = true;
        services.AddOmniRelayTelemetry(options);
    }

    public static void AddGrpcTelemetry(this IServiceCollection services)
    {
        var options = new OmniRelayTelemetryOptions
        {
            EnableTelemetry = true,
            EnableMetrics = true,
            EnableTracing = true
        };
        options.Prometheus.Enabled = true;
        services.AddOmniRelayTelemetry(options);
    }
}
