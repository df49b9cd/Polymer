namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Describes a bootstrap token to be issued.</summary>
public sealed class BootstrapTokenDescriptor
{
    public string ClusterId { get; init; } = "default";

    public string Role { get; init; } = "worker";

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(1);

    public int? MaxUses { get; init; }

    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
