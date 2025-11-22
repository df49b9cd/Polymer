namespace OmniRelay.Security.Secrets;

/// <summary>
/// Records secret access attempts for auditing without leaking secret values.
/// </summary>
public interface ISecretAccessAuditor
{
    /// <summary>Logs the outcome of a secret access request.</summary>
    /// <param name="providerName">Provider handling the request.</param>
    /// <param name="secretName">Logical name of the secret.</param>
    /// <param name="outcome">Result of the attempt.</param>
    /// <param name="exception">Optional exception.</param>
    void RecordAccess(string providerName, string secretName, SecretAccessOutcome outcome, Exception? exception = null);
}
