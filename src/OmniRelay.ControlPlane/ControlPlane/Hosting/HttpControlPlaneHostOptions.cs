using Microsoft.AspNetCore.Server.Kestrel.Https;
using OmniRelay.ControlPlane.Security;
using OmniRelay.Transport.Http;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Configuration for the shared HTTP control-plane host builder.</summary>
public sealed class HttpControlPlaneHostOptions
{
    public IList<string> Urls { get; } = new List<string> { "https://0.0.0.0:8080" };

    public HttpServerRuntimeOptions? Runtime { get; set; }

    public HttpServerTlsOptions? Tls { get; set; }

    public TransportTlsManager? SharedTls { get; set; }

    public ClientCertificateMode ClientCertificateMode { get; set; } = ClientCertificateMode.NoCertificate;

    public bool? CheckCertificateRevocation { get; set; }
}
