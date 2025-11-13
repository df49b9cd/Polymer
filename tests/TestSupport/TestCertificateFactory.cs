using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OmniRelay.Tests.Support;

internal static class TestCertificateFactory
{
    internal const string DevCertPassword = "applepie";
    private static readonly ConcurrentDictionary<string, Lazy<TestCertificateInfo>> Certificates =
        new(StringComparer.OrdinalIgnoreCase);

    public static X509Certificate2 CreateLoopbackCertificate(string subjectName)
    {
        if (string.IsNullOrWhiteSpace(subjectName))
        {
            throw new ArgumentException("Subject name is required.", nameof(subjectName));
        }

        var info = EnsureDeveloperCertificateInfo(subjectName);
        return info.CreateCertificate();
    }

    public static TestCertificateInfo EnsureDeveloperCertificateInfo(string subjectName)
    {
        if (string.IsNullOrWhiteSpace(subjectName))
        {
            throw new ArgumentException("Subject name is required.", nameof(subjectName));
        }

        return Certificates
            .GetOrAdd(subjectName, name => new Lazy<TestCertificateInfo>(() => CreateCertificateInfo(name)))
            .Value;
    }

    private static TestCertificateInfo CreateCertificateInfo(string subjectName)
    {
        using var certificate = CreateEphemeralLoopbackCertificate(subjectName);
        var export = certificate.Export(X509ContentType.Pfx, DevCertPassword);
        try
        {
            var data = Convert.ToBase64String(export);
            return new TestCertificateInfo(subjectName, DevCertPassword, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(export);
        }
    }

    private static X509Certificate2 CreateEphemeralLoopbackCertificate(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var eku = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1") // Server Authentication
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
