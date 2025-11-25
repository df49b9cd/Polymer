namespace OmniRelay.Identity;

/// <summary>Configures the MeshAgent control-plane client (watch, LKG cache, certificates).</summary>
public sealed class MeshAgentOptions
{
    /// <summary>Logical node identifier advertised to the control plane.</summary>
    public string NodeId { get; set; } = Environment.MachineName;

    /// <summary>Optional control-domain scope (prefixed onto the advertised node id).</summary>
    public string ControlDomain { get; set; } = "default";

    /// <summary>Capabilities advertised during the control watch handshake.</summary>
    public List<string> Capabilities { get; } = new() { "core/v1", "dsl/v1" };

    /// <summary>Path to the persisted last-known-good cache file.</summary>
    public string LkgPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "lkg", "control.json");

    /// <summary>Options for persisting and validating the LKG cache.</summary>
    public LkgCacheOptions LkgCache { get; set; } = new();

    /// <summary>Certificate issuance/renewal options (mTLS against control-plane/CA endpoints).</summary>
    public AgentCertificateOptions Certificates { get; set; } = new();

    /// <summary>When true, forces leadership to remain disabled even if leadership services are present.</summary>
    public bool DisableLeadership { get; set; } = true;
}

/// <summary>Certificate issuance and renewal settings for the local agent.</summary>
public sealed class AgentCertificateOptions
{
    /// <summary>Whether certificate issuance/renewal is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path to the agent PFX (private key + certificate chain).</summary>
    public string PfxPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "lkg", "agent.pfx");

    /// <summary>Password used to protect the persisted PFX bundle (can be null for no password).</summary>
    public string? PfxPassword { get; set; }

    /// <summary>Portion of the lifetime after which the agent should renew (0-1).</summary>
    public double RenewalWindow { get; set; } = 0.8;

    /// <summary>Minimum delay between renewal checks.</summary>
    public TimeSpan MinRenewalInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Backoff interval when renewal fails; increases exponentially.</summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>RSA key size used when generating CSRs.</summary>
    public int KeySize { get; set; } = 2048;

    /// <summary>Additional DNS SAN entries to include in the CSR.</summary>
    public List<string> SanDns { get; } = new();

    /// <summary>Additional URI SAN entries to include in the CSR.</summary>
    public List<string> SanUris { get; } = new();

    /// <summary>Optional path where the CA trust bundle will be written.</summary>
    public string TrustBundlePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "lkg", "trust-bundle.pem");
}

/// <summary>Integrity/signature options for the LKG cache.</summary>
public sealed class LkgCacheOptions
{
    /// <summary>Hash algorithm name used for integrity (e.g., SHA256, SHA512).</summary>
    public string HashAlgorithm { get; set; } = "SHA256";

    /// <summary>Optional signing key for HMAC signatures; when provided, signatures are required.</summary>
    public byte[]? SigningKey { get; set; }

    /// <summary>Whether the cache must contain a valid signature to be accepted.</summary>
    public bool RequireSignature { get; set; }
}
