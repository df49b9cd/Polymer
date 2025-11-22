using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Agent;

internal static partial class AgentLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "LKG applied version={Version}")]
    internal static partial void LkgApplied(ILogger logger, string version);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "control update rejected version={Version} error={Error}")]
    internal static partial void ControlUpdateRejected(ILogger logger, string version, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "control update applied version={Version}")]
    internal static partial void ControlUpdateApplied(ILogger logger, string version);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "control watch failed; backing off")]
    internal static partial void ControlWatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "control validation result={Result} duration_ms={DurationMs}")]
    internal static partial void ControlValidationResult(ILogger logger, bool result, double durationMs);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "agent: snapshot applied version={Version}")]
    internal static partial void SnapshotApplied(ILogger logger, string version);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "mesh agent started")]
    internal static partial void MeshAgentStarted(ILogger logger);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "mesh agent stopped")]
    internal static partial void MeshAgentStopped(ILogger logger);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "config applied (stub) version={Version} size={Size}")]
    internal static partial void ConfigAppliedStub(ILogger logger, string version, int size);
}
