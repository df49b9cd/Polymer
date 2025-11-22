namespace OmniRelay.Security.Authorization;

public readonly record struct MeshAuthorizationDecision(bool IsAllowed, string? Reason)
{
    public static MeshAuthorizationDecision Allowed { get; } = new(true, null);
}
