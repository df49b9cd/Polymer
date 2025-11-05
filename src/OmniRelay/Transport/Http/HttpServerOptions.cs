using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace OmniRelay.Transport.Http;

public sealed class HttpServerTlsOptions
{
    public X509Certificate2? Certificate { get; init; }

    public ClientCertificateMode ClientCertificateMode { get; init; } = ClientCertificateMode.NoCertificate;

    public bool? CheckCertificateRevocation { get; init; }
}

public sealed class HttpServerRuntimeOptions
{
    public long? MaxRequestBodySize { get; set; }
}
