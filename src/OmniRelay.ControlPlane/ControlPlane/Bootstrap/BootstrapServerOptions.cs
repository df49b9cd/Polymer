using OmniRelay.Identity;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Configures how bootstrap bundles are produced.</summary>
public sealed class BootstrapServerOptions
{
    public string ClusterId { get; set; } = "default";

    public string DefaultRole { get; set; } = "worker";

    public IList<string> SeedPeers { get; } = new List<string>();

    public string? BundlePassword { get; set; }

    public TransportTlsOptions Certificate { get; init; } = new();

    public TimeSpan JoinTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
