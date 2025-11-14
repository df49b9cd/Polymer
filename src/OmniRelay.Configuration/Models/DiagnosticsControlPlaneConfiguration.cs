using System.Collections.Generic;

namespace OmniRelay.Configuration.Models;

/// <summary>Configures the dedicated diagnostics/leadership control-plane hosts.</summary>
public sealed class DiagnosticsControlPlaneConfiguration
{
    /// <summary>HTTP control-plane bindings. Defaults to the classic dispatcher diagnostics endpoint.</summary>
    public IList<string> HttpUrls { get; } = ["http://127.0.0.1:8080"];

    public HttpServerRuntimeConfiguration HttpRuntime { get; init; } = new();

    public HttpServerTlsConfiguration HttpTls { get; init; } = new();

    /// <summary>gRPC control-plane bindings. Defaults to disabled until configured.</summary>
    public IList<string> GrpcUrls { get; } = [];

    public GrpcServerRuntimeConfiguration GrpcRuntime { get; init; } = new();

    public GrpcServerTlsConfiguration GrpcTls { get; init; } = new();

    /// <summary>Optional shared TLS configuration used by both hosts.</summary>
    public TransportTlsConfiguration Tls { get; init; } = new();
}
