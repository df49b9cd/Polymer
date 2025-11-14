namespace OmniRelay.ControlPlane.Clients;

public interface IHttpControlPlaneClientFactory
{
    HttpClient CreateClient(string profileName);

    HttpClient CreateClient(HttpControlPlaneClientProfile profile);
}
