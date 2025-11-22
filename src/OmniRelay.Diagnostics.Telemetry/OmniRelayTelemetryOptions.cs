namespace OmniRelay.Diagnostics;

/// <summary>Options describing OmniRelay telemetry exporters and runtime integration.</summary>
public sealed class OmniRelayTelemetryOptions
{
    public string ServiceName { get; set; } = "OmniRelay";

    public bool EnableTelemetry { get; set; } = true;

    public bool EnableMetrics { get; set; } = true;

    public bool EnableTracing { get; set; }

    public bool EnableRuntimeTraceSampler { get; set; }

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
    public bool Enabled { get; set; }

    public string? Endpoint { get; set; }

    public string Protocol { get; set; } = "grpc";
}
