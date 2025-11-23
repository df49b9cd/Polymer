namespace OmniRelay.Diagnostics;

/// <summary>Provides extension/plug-in snapshot data for diagnostics endpoints.</summary>
public interface IExtensionDiagnosticsProvider
{
    ExtensionDiagnosticsResponse CreateSnapshot();
}

/// <summary>Response payload for /control/extensions diagnostics.</summary>
public sealed record ExtensionDiagnosticsResponse(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ExtensionDiagnosticsEntry> Extensions);

/// <summary>Represents an individual extension entry.</summary>
public sealed record ExtensionDiagnosticsEntry(
    string Name,
    string Version,
    string Type,
    string Status,
    string? LastError,
    string? LastWatchdog,
    DateTimeOffset? LastLoadedAt,
    DateTimeOffset? LastExecutedAt,
    double? LastDurationMs,
    int FailureCount);
