using System.Collections.ObjectModel;
using OmniRelay.Identity;

namespace OmniRelay.Core.Gossip;

/// <summary>
/// Configuration options controlling the mesh gossip agent.
/// </summary>
public sealed class MeshGossipOptions
{
    /// <summary>The schema version emitted in gossip payloads.</summary>
    public const string CurrentSchemaVersion = "v1";

    /// <summary>Gets or sets whether the gossip agent is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Unique identifier for this node.</summary>
    public string NodeId { get; set; } = $"mesh-{Environment.MachineName}";

    /// <summary>Logical role for the node (dispatcher, gateway, worker, etc.).</summary>
    public string Role { get; set; } = "worker";

    /// <summary>Cluster identifier that scopes membership.</summary>
    public string ClusterId { get; set; } = "local";

    /// <summary>Region or fault-domain where this node is running.</summary>
    public string Region { get; set; } = "local";

    /// <summary>Version of the mesh runtime running on this node.</summary>
    public string MeshVersion { get; set; } = "dev";

    /// <summary>Indicates whether this node negotiated HTTP/3 for transports.</summary>
    public bool Http3Support { get; set; } = true;

    /// <summary>Optional labels appended to the metadata payload.</summary>
    public IDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Address the gossip listener binds to (default all interfaces).</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Port used by the gossip listener.</summary>
    public int Port { get; set; } = 17421;

    /// <summary>Advertised host or IP for other peers to dial (defaults to machine FQDN).</summary>
    public string? AdvertiseHost { get; set; }
        = Environment.GetEnvironmentVariable("HOSTNAME")
          ?? Environment.MachineName;

    /// <summary>Advertised port if different from bind port (defaults to <see cref="Port"/>).</summary>
    public int? AdvertisePort { get; set; }

    /// <summary>Interval between gossip rounds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of peers contacted per gossip round.</summary>
    public int Fanout { get; set; } = 3;

    /// <summary>Optional ceiling on fanout after adaptive scaling.</summary>
    public int FanoutCeiling { get; set; } = 32;

    /// <summary>Optional floor on computed fanout.</summary>
    public int FanoutFloor { get; set; } = 3;

    /// <summary>Coefficient applied to log2(clusterSize) when computing adaptive fanout.</summary>
    public double FanoutCoefficient { get; set; } = 1.5;

    /// <summary>When true, fanout scales with observed failure rate; otherwise fixed Fanout is used.</summary>
    public bool AdaptiveFanout { get; set; } = true;

    /// <summary>Maximum outbound gossip requests per round (backpressure guard).</summary>
    public int MaxOutboundPerRound { get; set; } = 20;

    /// <summary>Active partial view size (neighbors with open connections).</summary>
    public int ActiveViewSize { get; set; } = 16;

    /// <summary>Passive partial view size (standby neighbors used for healing).</summary>
    public int PassiveViewSize { get; set; } = 64;

    /// <summary>How often to perform passive-view shuffles.</summary>
    public TimeSpan ShuffleInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Number of peers exchanged during a shuffle.</summary>
    public int ShuffleSampleSize { get; set; } = 10;

    /// <summary>Duration without heartbeats before a peer becomes suspect.</summary>
    public TimeSpan SuspicionInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout applied to outbound gossip requests.</summary>
    public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum retransmits for join requests before considering a peer unreachable.</summary>
    public int RetransmitLimit { get; set; } = 3;

    /// <summary>Interval at which metadata is refreshed and re-broadcast.</summary>
    public TimeSpan MetadataRefreshPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval used to check for certificate rotation events.</summary>
    public TimeSpan CertificateReloadInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Optional endpoints preconfigured as seed peers.</summary>
    public IList<string> SeedPeers { get; init; } = [];

    /// <summary>TLS configuration for gossip traffic.</summary>
    public MeshGossipTlsOptions Tls { get; init; } = new();

    internal IReadOnlyList<string> GetNormalizedSeedPeers()
    {
        if (SeedPeers.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(SeedPeers.Count);
        foreach (var entry in SeedPeers)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            normalized.Add(entry.Trim());
        }

        return new ReadOnlyCollection<string>(normalized);
    }
}

/// <summary>
/// TLS configuration for the gossip listener and HTTP client.
/// </summary>
public sealed class MeshGossipTlsOptions
{
    /// <summary>Absolute or relative path to the PFX/PKCS12 certificate.</summary>
    public string? CertificatePath { get; set; }
        = Environment.GetEnvironmentVariable("MESH_GOSSIP_CERT_PATH");

    /// <summary>Base64-encoded PKCS12 certificate data (takes precedence over <see cref="CertificatePath" />).</summary>
    public string? CertificateData { get; set; }
        = Environment.GetEnvironmentVariable("MESH_GOSSIP_CERT_DATA");

    /// <summary>Secret name containing base64 PKCS12 certificate data.</summary>
    public string? CertificateDataSecret { get; set; }
        = Environment.GetEnvironmentVariable("MESH_GOSSIP_CERT_DATA_SECRET");

    /// <summary>Password for the certificate if required.</summary>
    public string? CertificatePassword { get; set; }
        = Environment.GetEnvironmentVariable("MESH_GOSSIP_CERT_PASSWORD");

    /// <summary>Secret resolving the certificate password.</summary>
    public string? CertificatePasswordSecret { get; set; }
        = Environment.GetEnvironmentVariable("MESH_GOSSIP_CERT_PASSWORD_SECRET");

    /// <summary>When true, certificate validation errors are ignored (dev use only).</summary>
    public bool AllowUntrustedCertificates { get; set; }
        = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether to check certificate revocation online.</summary>
    public bool CheckCertificateRevocation { get; set; } = true;

    /// <summary>Optional list of CA thumbprints required for peer certificates.</summary>
    public IList<string> AllowedThumbprints { get; init; } = [];

    /// <summary>Optional override for certificate reload interval.</summary>
    public TimeSpan? ReloadIntervalOverride { get; set; }

    internal TransportTlsOptions ToTransportTlsOptions(TimeSpan defaultReloadInterval)
    {
        var options = new TransportTlsOptions
        {
            CertificatePath = CertificatePath,
            CertificateData = CertificateData,
            CertificateDataSecret = CertificateDataSecret,
            CertificatePassword = CertificatePassword,
            CertificatePasswordSecret = CertificatePasswordSecret,
            AllowUntrustedCertificates = AllowUntrustedCertificates,
            CheckCertificateRevocation = CheckCertificateRevocation,
            ReloadInterval = ReloadIntervalOverride ?? defaultReloadInterval
        };

        foreach (var thumbprint in AllowedThumbprints)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                continue;
            }

            options.AllowedThumbprints.Add(thumbprint.Trim());
        }

        return options;
    }
}
