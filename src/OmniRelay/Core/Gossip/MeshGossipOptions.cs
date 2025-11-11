using System.Collections.ObjectModel;

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

    /// <summary>Password for the certificate if required.</summary>
    public string? CertificatePassword { get; set; }
        = Environment.GetEnvironmentVariable("MESH_GOSSIP_CERT_PASSWORD");

    /// <summary>When true, certificate validation errors are ignored (dev use only).</summary>
    public bool AllowUntrustedCertificates { get; set; }
        = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether to check certificate revocation online.</summary>
    public bool CheckCertificateRevocation { get; set; } = true;

    /// <summary>Optional list of CA thumbprints required for peer certificates.</summary>
    public IList<string> AllowedThumbprints { get; init; } = [];

    /// <summary>Optional override for certificate reload interval.</summary>
    public TimeSpan? ReloadIntervalOverride { get; set; }

}
