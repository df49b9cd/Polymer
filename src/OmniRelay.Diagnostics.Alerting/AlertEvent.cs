namespace OmniRelay.Diagnostics.Alerting;

/// <summary>Represents an alert emitted when critical events occur.</summary>
public sealed record AlertEvent(
    string Name,
    string Severity,
    string Message,
    IReadOnlyDictionary<string, string> Metadata);
