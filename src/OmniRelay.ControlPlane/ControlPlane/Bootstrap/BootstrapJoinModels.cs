namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Request payload for bootstrap join operations.</summary>
public sealed class BootstrapJoinRequest
{
    public string Token { get; set; } = string.Empty;

    public string? NodeId { get; set; }

    public string? RequestedRole { get; set; }

    public string? Environment { get; set; }

    public IDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public BootstrapAttestationEvidence? Attestation { get; set; }
}

/// <summary>Evidence supplied by a node to prove workload identity.</summary>
public sealed class BootstrapAttestationEvidence
{
    public string? Provider { get; set; }

    public string? Document { get; set; }

    public string? Signature { get; set; }

    public IDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Response payload containing bootstrap materials.</summary>
public sealed class BootstrapJoinResponse
{
    public string ClusterId { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string Identity { get; init; } = string.Empty;

    public string IdentityProvider { get; init; } = string.Empty;

    public string CertificateData { get; init; } = string.Empty;

    public string? CertificatePassword { get; init; }

    public string? TrustBundleData { get; init; }

    public IReadOnlyList<string> SeedPeers { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.MinValue;

    public DateTimeOffset RenewAfter { get; init; } = DateTimeOffset.MinValue;

    public DateTimeOffset ExpiresAt { get; init; }

    public string? AuditId { get; init; }
}
