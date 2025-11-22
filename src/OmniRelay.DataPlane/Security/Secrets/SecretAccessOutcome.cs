namespace OmniRelay.Security.Secrets;

/// <summary>Outcome of a secret access attempt for auditing.</summary>
public enum SecretAccessOutcome
{
    /// <summary>The secret was resolved successfully.</summary>
    Success,
    /// <summary>The provider did not contain the named secret.</summary>
    NotFound,
    /// <summary>The provider returned an error.</summary>
    Failed
}
