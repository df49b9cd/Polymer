namespace OmniRelay.Configuration.Models;

/// <summary>
/// Root diagnostics configuration including OpenTelemetry and runtime control-plane toggles.
/// </summary>
public sealed class DiagnosticsConfiguration
{
    public OpenTelemetryConfiguration OpenTelemetry { get; init; } = new();

    public RuntimeDiagnosticsConfiguration Runtime { get; init; } = new();

    public DiagnosticsControlPlaneConfiguration ControlPlane { get; init; } = new();
}

/// <summary>
/// OpenTelemetry configuration for metrics and tracing exporters.
/// </summary>
public sealed class OpenTelemetryConfiguration
{
    public bool? Enabled { get; set; }

    public string? ServiceName { get; set; }

    public bool? EnableMetrics { get; set; }

    public bool? EnableTracing { get; set; }

    public OtlpExporterConfiguration Otlp { get; init; } = new();

    public PrometheusExporterConfiguration Prometheus { get; init; } = new();
}

/// <summary>OTLP exporter configuration.</summary>
public sealed class OtlpExporterConfiguration
{
    public bool? Enabled { get; set; }

    public string? Endpoint { get; set; }

    public string? Protocol { get; set; }
}

/// <summary>Prometheus exporter configuration.</summary>
public sealed class PrometheusExporterConfiguration
{
    public bool? Enabled { get; set; }

    public string? ScrapeEndpointPath { get; set; }
}

/// <summary>Runtime control-plane toggles for logging and trace sampling.</summary>
public sealed class RuntimeDiagnosticsConfiguration
{
    public bool? EnableControlPlane { get; set; }

    public bool? EnableLoggingLevelToggle { get; set; }

    public bool? EnableTraceSamplingToggle { get; set; }
}
