namespace OmniRelay.Diagnostics;

/// <summary>Options describing OmniRelay telemetry exporters and runtime integration.</summary>
public sealed class OmniRelayTelemetryOptions
{
    public string ServiceName { get; set; } = "OmniRelay";

    public bool EnableTelemetry { get; set; } = true;

    public bool EnableMetrics { get; set; } = true;

    public bool EnableTracing { get; set; }
        = false;

    public bool EnableRuntimeTraceSampler { get; set; }
        = false;

    public PrometheusExporterOptions Prometheus { get; } = new();

    public OtlpExporterOptions Otlp { get; } = new();
}

public sealed class PrometheusExporterOptions
{
    public bool Enabled { get; set; } = true;

    public string? ScrapeEndpointPath { get; set; }
        = "/omnirelay/metrics";
}

public sealed class OtlpExporterOptions
{
    public bool Enabled { get; set; } = false;

    public string? Endpoint { get; set; }
        = null;

    public string Protocol { get; set; } = "grpc";
}
