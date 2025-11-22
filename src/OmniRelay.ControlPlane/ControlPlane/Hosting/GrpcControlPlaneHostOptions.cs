using Microsoft.AspNetCore.Server.Kestrel.Https;
using OmniRelay.ControlPlane.Security;
using OmniRelay.Transport.Grpc;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Configuration controlling the shared gRPC control-plane host builder.</summary>
public sealed class GrpcControlPlaneHostOptions
{
    /// <summary>Endpoints to bind (https:// required for HTTP/3).</summary>
    public IList<string> Urls { get; } = new List<string> { "https://0.0.0.0:17421" };

    /// <summary>Runtime options controlling HTTP/3, limits, and interceptors.</summary>
    public GrpcServerRuntimeOptions? Runtime { get; set; }

    /// <summary>Server TLS options (certificate + policy). When omitted, <see cref="SharedTls"/> is used.</summary>
    public GrpcServerTlsOptions? Tls { get; set; }

    /// <summary>Optional shared TLS manager used when <see cref="Tls"/> is not provided.</summary>
    public TransportTlsManager? SharedTls { get; set; }

    /// <summary>Controls whether mutual TLS is enforced when generating TLS options from <see cref="SharedTls"/>.</summary>
    public ClientCertificateMode ClientCertificateMode { get; set; } = ClientCertificateMode.RequireCertificate;

    /// <summary>Overrides revocation checks when generating TLS options from <see cref="SharedTls"/>.</summary>
    public bool? CheckCertificateRevocation { get; set; }

    /// <summary>Optional compression configuration.</summary>
    public GrpcCompressionOptions? Compression { get; set; }

    /// <summary>Optional telemetry toggles.</summary>
    public GrpcTelemetryOptions? Telemetry { get; set; }

    /// <summary>Additional gRPC server configuration hook.</summary>
    public Action<Grpc.AspNetCore.Server.GrpcServiceOptions>? ConfigureGrpc { get; set; }
}
