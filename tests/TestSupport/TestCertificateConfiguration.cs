namespace OmniRelay.Tests.Support;

internal static class TestCertificateConfiguration
{
    public static IReadOnlyDictionary<string, string?> BuildTlsDefaults(TestCertificateInfo certificate, string scope)
    {
        if (certificate is null)
        {
            throw new ArgumentNullException(nameof(certificate));
        }

        var normalizedScope = string.IsNullOrWhiteSpace(scope)
            ? "Tests"
            : scope.Replace(" ", string.Empty, StringComparison.Ordinal);

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["omniRelay:inbounds:http:0:name"] = $"{normalizedScope}HttpsInbound",
            ["omniRelay:inbounds:http:0:urls:0"] = "https://127.0.0.1:0",
            ["omniRelay:inbounds:http:0:tls:certificateData"] = certificate.CertificateData,
            ["omniRelay:inbounds:http:0:tls:certificatePassword"] = certificate.Password,
            ["omniRelay:inbounds:http:0:tls:clientCertificateMode"] = "AllowCertificate",
            ["omniRelay:inbounds:http:0:tls:checkCertificateRevocation"] = "false",

            ["omniRelay:inbounds:grpc:0:name"] = $"{normalizedScope}GrpcInbound",
            ["omniRelay:inbounds:grpc:0:urls:0"] = "https://127.0.0.1:0",
            ["omniRelay:inbounds:grpc:0:tls:certificateData"] = certificate.CertificateData,
            ["omniRelay:inbounds:grpc:0:tls:certificatePassword"] = certificate.Password,
            ["omniRelay:inbounds:grpc:0:tls:clientCertificateMode"] = "RequireCertificate",
            ["omniRelay:inbounds:grpc:0:tls:checkCertificateRevocation"] = "false",

            ["omniRelay:outbounds:loopback:unary:grpc:0:remoteService"] = "loopback",
            ["omniRelay:outbounds:loopback:unary:grpc:0:addresses:0"] = "https://127.0.0.1:5901",
            ["omniRelay:outbounds:loopback:unary:grpc:0:tls:certificateData"] = certificate.CertificateData,
            ["omniRelay:outbounds:loopback:unary:grpc:0:tls:certificatePassword"] = certificate.Password,
            ["omniRelay:outbounds:loopback:unary:grpc:0:tls:allowUntrustedCertificates"] = "true"
        };

        return data;
    }
}
