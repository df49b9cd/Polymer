namespace OmniRelay.ControlPlane.Clients;

public sealed class HttpControlPlaneClientFactoryOptions
{
    public IDictionary<string, HttpControlPlaneClientProfile> Profiles { get; } =
        new Dictionary<string, HttpControlPlaneClientProfile>(StringComparer.OrdinalIgnoreCase);
}
