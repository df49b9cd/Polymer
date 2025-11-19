namespace OmniRelay.Configuration.Models;

/// <summary>Security-focused configuration (secrets, enforcement policies, authorization, alerting, bootstrap).</summary>
public sealed class SecurityConfiguration
{
    public SecretsConfiguration Secrets { get; init; } = new();

    public TransportSecurityConfiguration Transport { get; init; } = new();

    public AuthorizationConfiguration Authorization { get; init; } = new();

    public AlertingConfiguration Alerting { get; init; } = new();

    public BootstrapConfiguration Bootstrap { get; init; } = new();
}

/// <summary>Describes secret providers and inline overrides.</summary>
public sealed class SecretsConfiguration
{
    public IList<SecretProviderConfiguration> Providers { get; } = new List<SecretProviderConfiguration>();

    public IDictionary<string, string> Inline { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Secret provider descriptor (environment, file, inline, custom).</summary>
public sealed class SecretProviderConfiguration
{
    public string? Type { get; set; }

    public string? Prefix { get; set; }

    public string? BasePath { get; set; }

    public IDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Transport security enforcement policy configuration.</summary>
public sealed class TransportSecurityConfiguration
{
    public bool? Enabled { get; set; }

    public IList<string> AllowedProtocols { get; } = new List<string>();

    public IList<string> AllowedTlsVersions { get; } = new List<string>();

    public IList<string> AllowedCipherSuites { get; } = new List<string>();

    public IList<string> AllowedThumbprints { get; } = new List<string>();

    public IList<EndpointPolicyConfiguration> Endpoints { get; } = new List<EndpointPolicyConfiguration>();

    public bool? RequireClientCertificates { get; set; }
}

/// <summary>Endpoint allow/deny rule.</summary>
public sealed class EndpointPolicyConfiguration
{
    public string? Host { get; set; }

    public string? Cidr { get; set; }

    public bool Allow { get; set; } = true;
}

/// <summary>Authorization policy set.</summary>
public sealed class AuthorizationConfiguration
{
    public bool? Enabled { get; set; }

    public IList<AuthorizationPolicyConfiguration> Policies { get; } = new List<AuthorizationPolicyConfiguration>();
}

/// <summary>Individual authorization policy definition.</summary>
public sealed class AuthorizationPolicyConfiguration
{
    public string? Name { get; set; }

    public IList<string> Roles { get; } = new List<string>();

    public IList<string> Clusters { get; } = new List<string>();

    public IDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IList<string> Principals { get; } = new List<string>();

    public bool? RequireMutualTls { get; set; }

    public string? AppliesTo { get; set; }
}

/// <summary>Alerting configuration for diagnostics and incident notifications.</summary>
public sealed class AlertingConfiguration
{
    public bool? Enabled { get; set; }

    public IList<AlertChannelConfiguration> Channels { get; } = new List<AlertChannelConfiguration>();

    public IDictionary<string, AlertTemplateConfiguration> Templates { get; init; } =
        new Dictionary<string, AlertTemplateConfiguration>(StringComparer.OrdinalIgnoreCase);

    public string? DefaultTemplate { get; set; }
}

/// <summary>A notification channel (webhook, Slack, PagerDuty, etc.).</summary>
public sealed class AlertChannelConfiguration
{
    public string? Name { get; set; }

    public string? Type { get; set; }

    public string? Endpoint { get; set; }

    public string? Template { get; set; }

    public string? AuthenticationSecret { get; set; }

    public TimeSpan? Cooldown { get; set; }

    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Template for alert payloads.</summary>
public sealed class AlertTemplateConfiguration
{
    public string? Title { get; set; }

    public string? Body { get; set; }
}

/// <summary>Bootstrap and join configuration for token/certificate issuance.</summary>
public sealed class BootstrapConfiguration
{
    public bool? Enabled { get; set; }

    public IList<string> HttpUrls { get; } = new List<string>();

    public TransportTlsConfiguration Tls { get; init; } = new();

    public IList<string> SeedPeers { get; } = new List<string>();

    public string? ClusterId { get; set; }

    public string? DefaultRole { get; set; }

    public string? BundlePassword { get; set; }

    public BootstrapSigningConfiguration Signing { get; init; } = new();

    public IList<BootstrapTokenConfiguration> Tokens { get; } = new List<BootstrapTokenConfiguration>();

    public string? SeedDirectory { get; set; }

    public BootstrapIdentityProviderConfiguration Identity { get; init; } = new();

    public BootstrapPolicyCollectionConfiguration Policies { get; init; } = new();

    public BootstrapLifecycleConfiguration Lifecycle { get; init; } = new();

    public bool? RequireAttestation { get; set; }
}

/// <summary>Signing options for bootstrap tokens.</summary>
public sealed class BootstrapSigningConfiguration
{
    public string? SigningKey { get; set; }

    public string? SigningKeySecret { get; set; }

    public TimeSpan? DefaultLifetime { get; set; }

    public int? MaxUses { get; set; }

    public string? Issuer { get; set; }
}

/// <summary>Join token definition.</summary>
public sealed class BootstrapTokenConfiguration
{
    public string? Name { get; set; }

    public string? Cluster { get; set; }

    public string? Role { get; set; }

    public TimeSpan? Lifetime { get; set; }

    public int? MaxUses { get; set; }

    public string? Secret { get; set; }
}

/// <summary>Workload identity provider wiring (SPIFFE/SPIRE, file-based, cloud).</summary>
public sealed class BootstrapIdentityProviderConfiguration
{
    public string? Type { get; set; }

    public SpiffeIdentityProviderConfiguration Spiffe { get; init; } = new();

    public FileIdentityProviderConfiguration File { get; init; } = new();
}

/// <summary>SPIFFE/SPIRE identity provider configuration.</summary>
public sealed class SpiffeIdentityProviderConfiguration
{
    public string? TrustDomain { get; set; }

    public string? SigningCertificatePath { get; set; }

    public string? SigningCertificateData { get; set; }

    public string? SigningCertificateDataSecret { get; set; }

    public string? SigningCertificatePassword { get; set; }

    public string? IdentityTemplate { get; set; }

    public TimeSpan? CertificateLifetime { get; set; }

    public IDictionary<string, string> DefaultMetadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? TrustBundleData { get; set; }

    public string? TrustBundleSecret { get; set; }
}

/// <summary>Development/file-based identity provider fallbacks.</summary>
public sealed class FileIdentityProviderConfiguration
{
    public string? CertificatePath { get; set; }

    public string? CertificateData { get; set; }

    public string? CertificatePassword { get; set; }

    public bool? AllowExport { get; set; }
}

/// <summary>Policy CRDs controlling which workloads may join.</summary>
public sealed class BootstrapPolicyCollectionConfiguration
{
    public IList<BootstrapPolicyDocumentConfiguration> Documents { get; } = new List<BootstrapPolicyDocumentConfiguration>();

    public string? Directory { get; set; }

    public bool? Watch { get; set; }
}

/// <summary>Individual policy document definition.</summary>
public sealed class BootstrapPolicyDocumentConfiguration
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public bool? DefaultAllow { get; set; }

    public IList<BootstrapPolicyRuleConfiguration> Rules { get; } = new List<BootstrapPolicyRuleConfiguration>();
}

/// <summary>Policy rule describing a set of constraints.</summary>
public sealed class BootstrapPolicyRuleConfiguration
{
    public string? Description { get; set; }

    public IList<string> Roles { get; } = new List<string>();

    public IList<string> Clusters { get; } = new List<string>();

    public IList<string> Environments { get; } = new List<string>();

    public IDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IList<string> AttestationProviders { get; } = new List<string>();

    public bool Allow { get; set; } = true;

    public string? IdentityTemplate { get; set; }

    public TimeSpan? Lifetime { get; set; }

    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Certificate lifecycle controls for issuance, renewal, and alerts.</summary>
public sealed class BootstrapLifecycleConfiguration
{
    public bool? EnableAutoRenewal { get; set; }

    public TimeSpan? RenewalLeadTime { get; set; }

    public TimeSpan? RenewalJitter { get; set; }

    public TimeSpan? FailureBackoff { get; set; }

    public bool? EnableAlerts { get; set; }
}
