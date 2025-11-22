using Microsoft.Extensions.Logging;

namespace OmniRelay.Security.Secrets;

/// <summary>
/// Default auditor that writes structured log events for secret activity.
/// </summary>
public sealed class LoggingSecretAccessAuditor(ILogger<LoggingSecretAccessAuditor> logger) : ISecretAccessAuditor
{
    private static readonly Action<ILogger, string, string, Exception?> SecretSuccessLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(1110, "SecretResolved"),
            "Secret {SecretName} resolved via {Provider}");

    private static readonly Action<ILogger, string, string, Exception?> SecretNotFoundLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1111, "SecretMissing"),
            "Secret {SecretName} not found via {Provider}");

    private static readonly Action<ILogger, string, string, Exception?> SecretFailureLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1112, "SecretResolutionFailed"),
            "Secret {SecretName} failed to resolve via {Provider}");

    public void RecordAccess(string providerName, string secretName, SecretAccessOutcome outcome, Exception? exception = null)
    {
        switch (outcome)
        {
            case SecretAccessOutcome.Success:
                SecretSuccessLog(logger, secretName, providerName, null);
                break;
            case SecretAccessOutcome.NotFound:
                SecretNotFoundLog(logger, secretName, providerName, null);
                break;
            default:
                SecretFailureLog(logger, secretName, providerName, exception);
                break;
        }
    }
}
