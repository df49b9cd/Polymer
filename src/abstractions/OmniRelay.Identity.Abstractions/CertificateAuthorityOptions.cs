namespace OmniRelay.Identity;

public sealed class CertificateAuthorityOptions
{
    public string IssuerName { get; set; } = "CN=OmniRelay Root";

    public string? RootPfxPath { get; set; }
        = "certs/omnirelay-ca.pfx";

    public string? RootPfxPassword { get; set; }
        = "changeit";

    public TimeSpan RootLifetime { get; set; } = TimeSpan.FromDays(3650);

    public TimeSpan LeafLifetime { get; set; } = TimeSpan.FromHours(12);

    public double RenewalWindow { get; set; } = 0.8;

    public string TrustDomain { get; set; } = "omnirelay.mesh";

    public bool RequireNodeBinding { get; set; }
        = true;

    public TimeSpan RootReloadInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int KeySize { get; set; } = 3072;
}
