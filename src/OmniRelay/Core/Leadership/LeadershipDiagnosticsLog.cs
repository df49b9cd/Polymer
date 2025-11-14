using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Leadership;

internal static partial class LeadershipDiagnosticsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Leadership SSE stream opened for scope {Scope} over {Transport}.")]
    public static partial void LeadershipStreamOpened(ILogger logger, string scope, string transport);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Leadership SSE stream closed for scope {Scope}.")]
    public static partial void LeadershipStreamClosed(ILogger logger, string scope);
}
