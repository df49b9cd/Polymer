using Grpc.Net.Client;

namespace OmniRelay.ControlPlane.Clients;

public interface IGrpcControlPlaneClientFactory
{
    GrpcChannel CreateChannel(string profileName);

    GrpcChannel CreateChannel(GrpcControlPlaneClientProfile profile);
}
