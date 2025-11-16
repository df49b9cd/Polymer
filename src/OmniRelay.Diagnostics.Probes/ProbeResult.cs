namespace OmniRelay.Diagnostics;

/// <summary>Represents the outcome of a probe execution.</summary>
public sealed record ProbeResult(bool Succeeded, TimeSpan Duration, string? Error = null, IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static ProbeResult Success(TimeSpan duration, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(true, duration, null, metadata);

    public static ProbeResult Failure(TimeSpan duration, string error, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(false, duration, error, metadata);
}
