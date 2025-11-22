namespace OmniRelay.Diagnostics;

/// <summary>Request payload for /omnirelay/control/logging.</summary>
public sealed record DiagnosticsLogLevelRequest(string? Level);

/// <summary>Request payload for /omnirelay/control/tracing.</summary>
public sealed record DiagnosticsSamplingRequest(double Probability);
