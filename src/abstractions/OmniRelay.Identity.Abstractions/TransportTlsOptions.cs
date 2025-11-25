using System.Security.Cryptography.X509Certificates;

namespace OmniRelay.Identity;

/// <summary>
/// Describes the source for control-plane TLS material (file path or inline data) plus policy hooks.
/// </summary>
public sealed class TransportTlsOptions
{
    /// <summary>Absolute or relative path to a PKCS12/PFX bundle.</summary>
    public string? CertificatePath { get; set; }
        = Environment.GetEnvironmentVariable("OMNIRELAY_TRANSPORT_CERT_PATH");

    /// <summary>Base64 encoded PKCS12 data. Takes precedence over <see cref="CertificatePath"/>.</summary>
    public string? CertificateData { get; set; }
        = Environment.GetEnvironmentVariable("OMNIRELAY_TRANSPORT_CERT_DATA");

    /// <summary>Name of the secret containing base64 encoded PKCS12 data.</summary>
    public string? CertificateDataSecret { get; set; }
        = Environment.GetEnvironmentVariable("OMNIRELAY_TRANSPORT_CERT_DATA_SECRET");

    /// <summary>Password protecting the certificate, if any.</summary>
    public string? CertificatePassword { get; set; }
        = Environment.GetEnvironmentVariable("OMNIRELAY_TRANSPORT_CERT_PASSWORD");

    /// <summary>Name of the secret providing the certificate password.</summary>
    public string? CertificatePasswordSecret { get; set; }
        = Environment.GetEnvironmentVariable("OMNIRELAY_TRANSPORT_CERT_PASSWORD_SECRET");

    /// <summary>Optional reload interval used when monitoring certificate files for rotation.</summary>
    public TimeSpan? ReloadInterval { get; set; }
        = TimeSpan.FromMinutes(5);

    /// <summary>Indicates whether untrusted certificates should be allowed (dev/test only).</summary>
    public bool AllowUntrustedCertificates { get; set; }
        = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>Indicates whether revocation should be checked when validating peer certificates.</summary>
    public bool CheckCertificateRevocation { get; set; } = true;

    /// <summary>Optional CA/thumbprint allow-list enforced on incoming client certificates.</summary>
    public ISet<string> AllowedThumbprints { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key storage flags used when importing certificates.</summary>
    public X509KeyStorageFlags KeyStorageFlags { get; set; }
        = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable;
}
