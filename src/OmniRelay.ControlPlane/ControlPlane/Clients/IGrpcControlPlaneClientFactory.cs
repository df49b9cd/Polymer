using Grpc.Net.Client;
using Hugo;

namespace OmniRelay.ControlPlane.Clients;

public interface IGrpcControlPlaneClientFactory
{
    Result<GrpcChannel> CreateChannel(string profileName, CancellationToken cancellationToken = default);

    Result<GrpcChannel> CreateChannel(GrpcControlPlaneClientProfile profile, CancellationToken cancellationToken = default);
}
