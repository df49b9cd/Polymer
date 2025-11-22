namespace OmniRelay.Diagnostics;

/// <summary>Snapshot of the most recent probe execution.</summary>
public sealed record ProbeExecutionSnapshot(
    string Name,
    DateTimeOffset LastExecutedAt,
    bool Succeeded,
    TimeSpan Duration,
    string? Error,
    IReadOnlyDictionary<string, string>? Metadata)
{
}
