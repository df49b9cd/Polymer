using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Bootstrap;
using OmniRelay.ControlPlane.Security;
using Xunit;

namespace OmniRelay.Core.UnitTests.Bootstrap;

public sealed class BootstrapServerTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task JoinAsync_ReturnsBundleWithCertificate()
    {
        var signingOptions = new BootstrapTokenSigningOptions
        {
            SigningKey = Encoding.UTF8.GetBytes("bundle-signing-key"),
            Issuer = "test-suite",
            DefaultLifetime = TimeSpan.FromMinutes(10)
        };
        var tokenService = new BootstrapTokenService(signingOptions, new InMemoryBootstrapReplayProtector(), NullLogger<BootstrapTokenService>.Instance);

        var certificateBytes = CreateCertificateBytes("CN=bootstrap-test", "bundle-pass");
        var serverOptions = new BootstrapServerOptions
        {
            ClusterId = "cluster-1",
            DefaultRole = "worker",
            BundlePassword = "bundle-pass",
            Certificate = new TransportTlsOptions
            {
                CertificateData = Convert.ToBase64String(certificateBytes),
                CertificatePassword = "bundle-pass"
            }
        };
        serverOptions.SeedPeers.Add("https://seed-a:8080");
        serverOptions.SeedPeers.Add("https://seed-b:8080");

        using var tlsManager = new TransportTlsManager(serverOptions.Certificate, NullLogger<TransportTlsManager>.Instance);
        var server = new BootstrapServer(serverOptions, tokenService, tlsManager, NullLogger<BootstrapServer>.Instance);

        var token = tokenService.CreateToken(new BootstrapTokenDescriptor { ClusterId = "cluster-1", Role = "worker" });
        var result = await server.JoinAsync(new BootstrapJoinRequest { Token = token }, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var response = result.ValueOrThrow();

        response.ClusterId.ShouldBe("cluster-1");
        response.Role.ShouldBe("worker");
        response.SeedPeers.ShouldContain("https://seed-a:8080");
        response.CertificateData.ShouldNotBeNullOrWhiteSpace();
    }

    private static byte[] CreateCertificateBytes(string subject, string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.Export(X509ContentType.Pfx, password);
    }
}
