using Microsoft.Extensions.Logging;

namespace OmniRelay.Diagnostics;

/// <summary>
/// Provides mutable diagnostics controls for logging and tracing at runtime.
/// </summary>
public interface IDiagnosticsRuntime
{
    /// <summary>Gets the minimum log level override.</summary>
    LogLevel? MinimumLogLevel { get; }

    /// <summary>Gets the trace sampling probability (0..1) override.</summary>
    double? TraceSamplingProbability { get; }

    /// <summary>Sets the minimum log level override.</summary>
    void SetMinimumLogLevel(LogLevel? level);

    /// <summary>Sets the trace sampling probability override.</summary>
    void SetTraceSamplingProbability(double? probability);
}
