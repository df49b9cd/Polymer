namespace OmniRelay.ControlPlane.Clients;

/// <summary>Options container describing named control-plane gRPC client profiles.</summary>
public sealed class GrpcControlPlaneClientFactoryOptions
{
    public IDictionary<string, GrpcControlPlaneClientProfile> Profiles { get; } =
        new Dictionary<string, GrpcControlPlaneClientProfile>(StringComparer.OrdinalIgnoreCase);
}
