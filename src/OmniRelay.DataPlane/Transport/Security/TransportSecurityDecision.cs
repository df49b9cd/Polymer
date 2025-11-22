namespace OmniRelay.Transport.Security;

/// <summary>Result of applying the transport security policy to a request.</summary>
public readonly record struct TransportSecurityDecision(bool IsAllowed, string? Reason)
{
    public static TransportSecurityDecision Allowed { get; } = new(true, null);

    public TransportSecurityDecisionPayload ToPayload(string transport, string endpoint) =>
        new(IsAllowed, transport, endpoint, Reason);
}

public readonly record struct TransportSecurityDecisionPayload(bool Allowed, string Transport, string Endpoint, string? Reason);
