namespace YARPCore.Configuration.Models;

public sealed class DiagnosticsConfiguration
{
    public OpenTelemetryConfiguration OpenTelemetry { get; init; } = new();

    public RuntimeDiagnosticsConfiguration Runtime { get; init; } = new();
}

public sealed class OpenTelemetryConfiguration
{
    public bool? Enabled { get; set; }

    public string? ServiceName { get; set; }

    public bool? EnableMetrics { get; set; }

    public bool? EnableTracing { get; set; }

    public OtlpExporterConfiguration Otlp { get; init; } = new();

    public PrometheusExporterConfiguration Prometheus { get; init; } = new();
}

public sealed class OtlpExporterConfiguration
{
    public bool? Enabled { get; set; }

    public string? Endpoint { get; set; }

    public string? Protocol { get; set; }
}

public sealed class PrometheusExporterConfiguration
{
    public bool? Enabled { get; set; }

    public string? ScrapeEndpointPath { get; set; }
}

public sealed class RuntimeDiagnosticsConfiguration
{
    public bool? EnableControlPlane { get; set; }

    public bool? EnableLoggingLevelToggle { get; set; }

    public bool? EnableTraceSamplingToggle { get; set; }
}
