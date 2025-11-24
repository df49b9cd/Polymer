namespace OmniRelay.ControlPlane.Identity;

public sealed class CertificateAuthorityOptions
{
    /// <summary>Distinguished name for the root CA.</summary>
    public string IssuerName { get; init; } = "CN=OmniRelay MeshKit CA";

    /// <summary>Lifetime for the root certificate.</summary>
    public TimeSpan RootLifetime { get; init; } = TimeSpan.FromDays(365);

    /// <summary>Lifetime for issued leaf certificates.</summary>
    public TimeSpan LeafLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Optional path to persist/load the root CA (PFX including private key). If omitted, an in-memory root is generated per process.</summary>
    public string? RootPfxPath { get; init; }

    /// <summary>Password for persisted root PFX (only used when RootPfxPath is specified).</summary>
    public string? RootPfxPassword { get; init; }
}
