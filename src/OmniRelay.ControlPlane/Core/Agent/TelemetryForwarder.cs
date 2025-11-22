using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Placeholder telemetry forwarder; plug into OTLP/exporters later.</summary>
public sealed class TelemetryForwarder
{
    private readonly ILogger<TelemetryForwarder> _logger;

    public TelemetryForwarder(ILogger<TelemetryForwarder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RecordSnapshot(string version)
    {
        AgentLog.SnapshotApplied(_logger, version);
    }
}
