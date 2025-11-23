using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Extensions;

internal static partial class ExtensionTelemetryLog
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "extension loaded: {Name} {Version} type={Type}")]
    internal static partial void ExtensionLoaded(ILogger logger, string name, string version, string type);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "extension rejected: {Name} {Version} reason={Reason}")]
    internal static partial void ExtensionRejected(ILogger logger, string name, string version, string reason);

    [LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "extension failure: {Name} {Version} reason={Reason}")]
    internal static partial void ExtensionFailure(ILogger logger, string name, string version, string reason);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning, Message = "extension watchdog trip: {Name} {Version} resource={Resource}")]
    internal static partial void ExtensionWatchdogTrip(ILogger logger, string name, string version, string resource);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "extension executed: {Name} {Version} duration_ms={DurationMs}")]
    internal static partial void ExtensionExecuted(ILogger logger, string name, string version, double durationMs);

    [LoggerMessage(EventId = 105, Level = LogLevel.Information, Message = "extension reloaded: {Name} {Version}")]
    internal static partial void ExtensionReloaded(ILogger logger, string name, string version);
}
