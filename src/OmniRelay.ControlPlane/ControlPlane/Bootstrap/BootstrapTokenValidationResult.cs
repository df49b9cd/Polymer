namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Result of validating a bootstrap token.</summary>
public sealed class BootstrapTokenValidationResult
{
    private BootstrapTokenValidationResult() { }

    public bool IsValid { get; private init; }

    public string? FailureReason { get; private init; }

    public Guid TokenId { get; private init; }

    public string ClusterId { get; private init; } = string.Empty;

    public string Role { get; private init; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private init; }

    public static BootstrapTokenValidationResult Success(Guid tokenId, string clusterId, string role, DateTimeOffset expiresAt) =>
        new()
        {
            IsValid = true,
            TokenId = tokenId,
            ClusterId = clusterId,
            Role = role,
            ExpiresAt = expiresAt
        };

    public static BootstrapTokenValidationResult Failed(string reason) =>
        new()
        {
            IsValid = false,
            FailureReason = reason
        };
}
