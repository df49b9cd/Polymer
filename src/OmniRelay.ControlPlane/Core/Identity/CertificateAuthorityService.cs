using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Pkcs;
using Grpc.Core;
using OmniRelay.Protos.Ca;

namespace OmniRelay.ControlPlane.Identity;

/// <summary>
/// Simple in-process CA service for MeshKit: accepts CSRs, issues short-lived leaf certs, and serves trust bundle.
/// NOTE: Placeholder implementation with ephemeral root; replace with real PKI before production.
/// </summary>
public sealed class CertificateAuthorityService : CertificateAuthority.CertificateAuthorityBase
{
    private static readonly X509Certificate2 RootCa = CreateRootCa();
    private static readonly byte[] TrustPem = ExportPem(new[] { RootCa });

    public override Task<CertResponse> SubmitCsr(CsrRequest request, ServerCallContext context)
    {
        using var caKey = RootCa.GetRSAPrivateKey()!;
        var cert = IssueLeaf(RootCa, caKey, request.NodeId);

        var response = new CertResponse
        {
            Certificate = Google.Protobuf.ByteString.CopyFrom(cert.Export(X509ContentType.Cert)),
            CertificateChain = Google.Protobuf.ByteString.CopyFrom(Concat(new[] { cert, RootCa })),
            TrustBundle = Google.Protobuf.ByteString.CopyFrom(TrustPem),
            ExpiresAt = cert.NotAfter.ToUniversalTime().ToString("O")
        };

        return Task.FromResult(response);
    }

    public override Task<TrustBundleResponse> TrustBundle(TrustBundleRequest request, ServerCallContext context) =>
        Task.FromResult(new TrustBundleResponse { TrustBundle = Google.Protobuf.ByteString.CopyFrom(TrustPem) });

    private static X509Certificate2 CreateRootCa()
    {
        using var rsa = RSA.Create(3072);
        var dn = new X500DistinguishedName("CN=OmniRelay MeshKit Dev CA");
        var req = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return cert.CopyWithPrivateKey(rsa);
    }

    private static X509Certificate2 IssueLeaf(X509Certificate2 issuer, RSA issuerKey, string nodeId)
    {
        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={nodeId}");
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(nodeId);
        req.CertificateExtensions.Add(sanBuilder.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var generator = X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1);
        var cert = req.Create(issuer.SubjectName, generator, now, now.AddDays(7), serial);
        return cert.CopyWithPrivateKey(rsa);
    }

    private static byte[] Concat(IEnumerable<X509Certificate2> certs)
    {
        using var ms = new MemoryStream();
        foreach (var cert in certs)
        {
            var raw = cert.Export(X509ContentType.Cert);
            ms.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    private static byte[] ExportPem(IEnumerable<X509Certificate2> certs)
    {
        using var writer = new StringWriter();
        foreach (var cert in certs)
        {
            writer.WriteLine("-----BEGIN CERTIFICATE-----");
            writer.WriteLine(Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks));
            writer.WriteLine("-----END CERTIFICATE-----");
        }
        return System.Text.Encoding.UTF8.GetBytes(writer.ToString());
    }
}
