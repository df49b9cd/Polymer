using OmniRelay.Transport.Http;

namespace OmniRelay.ControlPlane.Clients;

public sealed class HttpControlPlaneClientProfile
{
    public required Uri BaseAddress { get; init; }

    public bool UseSharedTls { get; init; } = true;

    public HttpClientRuntimeOptions? Runtime { get; init; }
}
