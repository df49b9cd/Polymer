namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Describes how bootstrap tokens are signed.</summary>
public sealed class BootstrapTokenSigningOptions
{
    public required byte[] SigningKey { get; init; }

    public string Issuer { get; init; } = "omnirelay-bootstrap";

    public TimeSpan DefaultLifetime { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(1);

    public int? DefaultMaxUses { get; init; }
}
