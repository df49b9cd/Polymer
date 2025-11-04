using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace YARPCore.Transport.Grpc;

public sealed record GrpcClientTlsOptions
{
    public X509Certificate2Collection ClientCertificates { get; init; } = [];

    public EncryptionPolicy? EncryptionPolicy { get; init; }

    public SslProtocols? EnabledProtocols { get; init; }

    public bool? CheckCertificateRevocation { get; init; }

    public RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; init; }
}

public sealed record GrpcServerTlsOptions
{
    public required X509Certificate2 Certificate { get; init; }

    public ClientCertificateMode ClientCertificateMode { get; init; } = ClientCertificateMode.NoCertificate;

    public bool? CheckCertificateRevocation { get; init; }

    public SslProtocols? EnabledProtocols { get; init; }

    public Func<X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? ClientCertificateValidation { get; init; }
}
