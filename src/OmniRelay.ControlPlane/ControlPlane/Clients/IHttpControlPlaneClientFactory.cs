using Hugo;

namespace OmniRelay.ControlPlane.Clients;

public interface IHttpControlPlaneClientFactory
{
    Result<HttpClient> CreateClient(string profileName, CancellationToken cancellationToken = default);

    Result<HttpClient> CreateClient(HttpControlPlaneClientProfile profile, CancellationToken cancellationToken = default);
}
