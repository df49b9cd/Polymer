using System.Collections.Concurrent;
using OmniRelay.Diagnostics;

namespace OmniRelay.Core.Extensions;

/// <summary>
/// Tracks loaded extensions and exposes status snapshots for diagnostics.
/// </summary>
internal sealed class ExtensionRegistry : IExtensionDiagnosticsProvider
{
    private const string SchemaVersion = "v1";
    private readonly ConcurrentDictionary<string, ExtensionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void RecordLoaded(ExtensionPackage package)
    {
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            package.Name,
            _ => new ExtensionEntry(package)
            {
                Status = "loaded",
                LastLoadedAt = now,
                LastError = null,
                LastWatchdog = null,
                FailureCount = 0
            },
            (_, entry) => entry with
            {
                Status = "loaded",
                LastLoadedAt = now,
                LastError = null,
                LastWatchdog = null
            });
    }

    public void RecordRejected(ExtensionPackage package, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            package.Name,
            _ => new ExtensionEntry(package)
            {
                Status = "rejected",
                LastError = reason,
                LastLoadedAt = null,
                FailureCount = 1
            },
            (_, entry) => entry with
            {
                Status = "rejected",
                LastError = reason,
                LastLoadedAt = entry.LastLoadedAt,
                FailureCount = entry.FailureCount + 1
            });
    }

    public void RecordExecuted(ExtensionPackage package, double durationMs)
    {
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            package.Name,
            _ => new ExtensionEntry(package)
            {
                Status = "executed",
                LastExecutedAt = now,
                LastDurationMs = durationMs,
                LastError = null,
                LastWatchdog = null
            },
            (_, entry) => entry with
            {
                Status = "executed",
                LastExecutedAt = now,
                LastDurationMs = durationMs,
                LastError = null,
                LastWatchdog = null
            });
    }

    public void RecordFailed(ExtensionPackage package, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            package.Name,
            _ => new ExtensionEntry(package)
            {
                Status = "failed",
                LastError = reason,
                LastExecutedAt = now,
                FailureCount = 1
            },
            (_, entry) => entry with
            {
                Status = "failed",
                LastError = reason,
                LastExecutedAt = now,
                FailureCount = entry.FailureCount + 1
            });
    }

    public void RecordWatchdog(ExtensionPackage package, string resource)
    {
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            package.Name,
            _ => new ExtensionEntry(package)
            {
                Status = "watchdog",
                LastWatchdog = resource,
                LastExecutedAt = now,
                FailureCount = 1
            },
            (_, entry) => entry with
            {
                Status = "watchdog",
                LastWatchdog = resource,
                LastExecutedAt = now,
                FailureCount = entry.FailureCount + 1
            });
    }

    public ExtensionDiagnosticsResponse CreateSnapshot()
    {
        var snapshotTime = DateTimeOffset.UtcNow;
        var entries = _entries.Values
            .OrderBy(e => e.Package.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ExtensionDiagnosticsEntry(
                e.Package.Name,
                e.Package.Version.ToString(),
                e.Package.Type.ToString().ToLowerInvariant(),
                e.Status,
                e.LastError,
                e.LastWatchdog,
                e.LastLoadedAt,
                e.LastExecutedAt,
                e.LastDurationMs,
                e.FailureCount))
            .ToList();

        return new ExtensionDiagnosticsResponse(SchemaVersion, snapshotTime, entries);
    }

    private sealed record ExtensionEntry(ExtensionPackage Package)
    {
        public string Status { get; init; } = "unknown";
        public string? LastError { get; init; }
        public string? LastWatchdog { get; init; }
        public DateTimeOffset? LastLoadedAt { get; init; }
        public DateTimeOffset? LastExecutedAt { get; init; }
        public double? LastDurationMs { get; init; }
        public int FailureCount { get; init; }
    }
}
