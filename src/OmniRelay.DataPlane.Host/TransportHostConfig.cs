using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.DataPlane.Host;

/// <summary>Configuration shape for data-plane transport plugin wiring.</summary>
internal sealed class TransportHostConfig
{
    public List<string> HttpUrls { get; init; } = ["http://localhost:8080"]; // legacy defaults

    public List<string> GrpcUrls { get; init; } = ["http://localhost:8090"]; // legacy defaults

    public HttpServerRuntimeOptions HttpRuntime { get; init; } = new() { EnableHttp3 = true };

    public HttpServerTlsOptions? HttpTls { get; init; }
        

    public GrpcServerRuntimeOptions GrpcRuntime { get; init; } = new() { EnableHttp3 = true };

    public GrpcServerTlsOptions? GrpcTls { get; init; }
}
