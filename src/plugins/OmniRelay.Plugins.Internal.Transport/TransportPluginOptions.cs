using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Plugins.Internal.Transport;

/// <summary>Options for registering built-in HTTP/3 and gRPC transports via the internal transport plugin.</summary>
public sealed class TransportPluginOptions
{
    /// <summary>HTTP listener URLs (Kestrel format). Leave empty to skip HTTP transport registration.</summary>
    public List<string> HttpUrls { get; } = new();

    /// <summary>gRPC listener URLs (Kestrel format). Leave empty to skip gRPC transport registration.</summary>
    public List<string> GrpcUrls { get; } = new();

    public HttpServerRuntimeOptions HttpRuntime { get; set; } = new() { EnableHttp3 = true };

    public HttpServerTlsOptions? HttpTls { get; set; }

    public GrpcServerRuntimeOptions GrpcRuntime { get; set; } = new() { EnableHttp3 = true };

    public GrpcServerTlsOptions? GrpcTls { get; set; }
}
