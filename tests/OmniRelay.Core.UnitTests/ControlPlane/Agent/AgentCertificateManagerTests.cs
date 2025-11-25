using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OmniRelay.Plugins.Internal.Identity;
using OmniRelay.Protos.Ca;
using Xunit;

namespace OmniRelay.Core.UnitTests.ControlPlane.Agent;

public sealed class AgentCertificateManagerTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task EnsureCurrentAsync_RenewsWhenWindowReached()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"agent-cert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var pfxPath = Path.Combine(tempDir, "agent.pfx");
        var bundlePath = Path.Combine(tempDir, "trust.pem");

        var options = Options.Create(new MeshAgentOptions
        {
            NodeId = "node-a",
            Certificates = new AgentCertificateOptions
            {
                PfxPath = pfxPath,
                TrustBundlePath = bundlePath,
                RenewalWindow = 0.5,
                MinRenewalInterval = TimeSpan.FromMilliseconds(10),
                FailureBackoff = TimeSpan.FromMilliseconds(5)
            }
        });

        var time = new FakeTimeProvider();
        var caClient = new FakeCaClient(time);
        var manager = new AgentCertificateManager(caClient, options, NullLogger<AgentCertificateManager>.Instance, time);

        try
        {
            var first = await manager.EnsureCurrentAsync(CancellationToken.None);
            first.IsSuccess.ShouldBeTrue(first.Error?.ToString());
            caClient.Submissions.ShouldBe(1);
            File.Exists(pfxPath).ShouldBeTrue();
            File.Exists(bundlePath).ShouldBeTrue();

        var firstExpires = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null, X509KeyStorageFlags.Exportable).NotAfter;

            // Advance past renew-after to force a new issuance.
            time.Advance(TimeSpan.FromMinutes(20));
            var second = await manager.EnsureCurrentAsync(CancellationToken.None);
            second.IsSuccess.ShouldBeTrue(second.Error?.ToString());
            caClient.Submissions.ShouldBe(2);

            var secondExpires = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null, X509KeyStorageFlags.Exportable).NotAfter;
            (secondExpires > firstExpires).ShouldBeTrue();
        }
        finally
        {
            manager.Dispose();
            TryDelete(pfxPath);
            TryDelete(bundlePath);
            TryDelete(tempDir);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed class FakeCaClient : ICertificateAuthorityClient
{
    private readonly FakeTimeProvider _timeProvider;

    public FakeCaClient(FakeTimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public int Submissions { get; private set; }

    public Task<CertResponse> SubmitCsrAsync(CsrRequest request, CancellationToken cancellationToken = default)
    {
        Submissions++;
        return Task.FromResult(BuildResponse(request));
    }

    public Task<TrustBundleResponse> TrustBundleAsync(TrustBundleRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrustBundleResponse());

    private CertResponse BuildResponse(CsrRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        var csr = CertificateRequest.LoadSigningRequest(request.Csr.ToByteArray(), HashAlgorithmName.SHA256);
        using var issuerKey = RSA.Create(2048);
        var issuerReq = new CertificateRequest("CN=test-ca", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuerReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuerReq.PublicKey, false));
        var issuerCert = issuerReq.CreateSelfSigned(now.AddMinutes(-10).UtcDateTime, now.AddDays(1).UtcDateTime);

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        var generator = X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1);
        var cert = csr.Create(new X500DistinguishedName("CN=test-ca"), generator, now.AddMinutes(-1).UtcDateTime, now.AddMinutes(30).UtcDateTime, serial);
        var der = cert.Export(X509ContentType.Cert);
        var issuerDer = issuerCert.Export(X509ContentType.Cert);

        return new CertResponse
        {
            Certificate = Google.Protobuf.ByteString.CopyFrom(der),
            CertificateChain = Google.Protobuf.ByteString.CopyFrom(issuerDer),
            TrustBundle = Google.Protobuf.ByteString.CopyFrom(issuerDer),
            ExpiresAt = cert.NotAfter.ToUniversalTime().ToString("O"),
            RenewAfter = now.AddMinutes(5).ToString("O"),
            IssuedAt = now.ToUniversalTime().ToString("O"),
            Subject = cert.Subject
        };
    }
}
