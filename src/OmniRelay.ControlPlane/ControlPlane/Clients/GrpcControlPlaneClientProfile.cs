using OmniRelay.Transport.Grpc;

namespace OmniRelay.ControlPlane.Clients;

/// <summary>Defines a named control-plane gRPC endpoint profile.</summary>
public sealed class GrpcControlPlaneClientProfile
{
    public required Uri Address { get; init; }

    public bool PreferHttp3 { get; init; } = true;

    public bool UseSharedTls { get; init; } = true;

    public GrpcClientRuntimeOptions? Runtime { get; init; }

    public GrpcClientTlsOptions? Tls { get; init; }
}
