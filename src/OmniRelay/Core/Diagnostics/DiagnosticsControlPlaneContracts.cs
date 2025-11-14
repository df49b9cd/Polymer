namespace OmniRelay.Core.Diagnostics;

internal sealed record DiagnosticsLogLevelRequest(string? Level);

internal sealed record DiagnosticsSamplingRequest(double Probability);
