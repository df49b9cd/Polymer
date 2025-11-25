using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OmniRelay.Identity;
using OmniRelay.Core.UnitTests.ControlPlane.ControlProtocol;
using OmniRelay.Protos.Ca;
using Xunit;

namespace OmniRelay.Core.UnitTests.ControlPlane.Identity;

public sealed class CertificateAuthorityServiceTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SubmitCsr_IssuesLeafCertificateAndTrustBundle()
    {
        var csr = CreateCsr("agent-1");
        var service = new CertificateAuthorityService(
            Options.Create(new CertificateAuthorityOptions { LeafLifetime = TimeSpan.FromHours(2), RenewalWindow = 0.5 }),
            NullLogger<CertificateAuthorityService>.Instance);

        var response = await service.SubmitCsr(
            new CsrRequest { NodeId = "agent-1", Csr = Google.Protobuf.ByteString.CopyFrom(csr) },
            new TestServerCallContext(CancellationToken.None));

        response.ShouldNotBeNull();
        response.ExpiresAt.ShouldNotBeNullOrWhiteSpace();
        response.IssuedAt.ShouldNotBeNullOrWhiteSpace();
        response.RenewAfter.ShouldNotBeNullOrWhiteSpace();
        response.SanDns.ShouldContain("agent-1");

        var pem = PemEncoding.Write("CERTIFICATE", response.Certificate.ToByteArray());
        var leaf = X509Certificate2.CreateFromPem(pem);
        leaf.Subject.ShouldContain("agent-1", Case.Insensitive);

        var trust = response.TrustBundle.ToByteArray();
        trust.ShouldNotBeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SubmitCsr_RejectsWhenNodeIdMissingFromCsr()
    {
        var csr = CreateCsr("other-node");
        var service = new CertificateAuthorityService(
            Options.Create(new CertificateAuthorityOptions()),
            NullLogger<CertificateAuthorityService>.Instance);

        await Should.ThrowAsync<RpcException>(async () =>
            await service.SubmitCsr(
                new CsrRequest { NodeId = "agent-2", Csr = Google.Protobuf.ByteString.CopyFrom(csr) },
                new TestServerCallContext(CancellationToken.None)));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SubmitCsr_ReloadsRootWhenFileChanges()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"omnirelay-ca-{Guid.NewGuid():N}.pfx");
        var password = "root-pass";
        try
        {
            var firstRoot = CreateRootPfx("CN=Root-1", password);
            File.WriteAllBytes(rootPath, firstRoot);

            var options = new CertificateAuthorityOptions
            {
                RootPfxPath = rootPath,
                RootPfxPassword = password,
                RootReloadInterval = TimeSpan.Zero
            };

            var service = new CertificateAuthorityService(Options.Create(options), NullLogger<CertificateAuthorityService>.Instance);
            var csr = CreateCsr("agent-3");

            var first = await service.SubmitCsr(new CsrRequest { NodeId = "agent-3", Csr = Google.Protobuf.ByteString.CopyFrom(csr) }, new TestServerCallContext(CancellationToken.None));
            var firstTrust = first.TrustBundle.ToByteArray();

            var secondRoot = CreateRootPfx("CN=Root-2", password);
            File.WriteAllBytes(rootPath, secondRoot);

            var second = await service.SubmitCsr(new CsrRequest { NodeId = "agent-3", Csr = Google.Protobuf.ByteString.CopyFrom(csr) }, new TestServerCallContext(CancellationToken.None));
            var secondTrust = second.TrustBundle.ToByteArray();

            secondTrust.SequenceEqual(firstTrust).ShouldBeFalse();
            second.Subject.ShouldContain("agent-3");
        }
        finally
        {
            if (File.Exists(rootPath))
            {
                File.Delete(rootPath);
            }
        }
    }

    private static byte[] CreateCsr(string nodeId, string? spiffeUri = null)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={nodeId}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(nodeId);
        if (!string.IsNullOrWhiteSpace(spiffeUri))
        {
            sanBuilder.AddUri(new Uri(spiffeUri));
        }

        req.CertificateExtensions.Add(sanBuilder.Build());
        return req.CreateSigningRequest();
    }

    private static byte[] CreateRootPfx(string subject, string password)
    {
        using var key = RSA.Create(3072);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = req.CreateSelfSigned(now.UtcDateTime, now.AddDays(30).UtcDateTime);
        return root.Export(X509ContentType.Pfx, password);
    }
}
