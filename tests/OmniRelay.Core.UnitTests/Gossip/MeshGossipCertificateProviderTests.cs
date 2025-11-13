using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Gossip;
using Shouldly;
using Xunit;

namespace OmniRelay.Core.UnitTests.Gossip;

public sealed class MeshGossipCertificateProviderTests
{
    [Fact]
    public void GetCertificate_ThrowsWhenCertificatePathMissing()
    {
        var options = CreateOptions();
        options.Tls.CertificatePath = null;
        options.Tls.CertificateData = null;

        using var provider = new MeshGossipCertificateProvider(options, NullLogger<MeshGossipCertificateProvider>.Instance);

        Should.Throw<InvalidOperationException>(() => provider.GetCertificate())
            .Message.ShouldContain("mesh:gossip:tls:certificatePath");
    }

    [Fact]
    public void GetCertificate_ThrowsWhenInlineCertificateDataIsInvalid()
    {
        var options = CreateOptions();
        options.Tls.CertificateData = "not-base64!";
        options.Tls.CertificatePassword = "pass";

        using var provider = new MeshGossipCertificateProvider(options, NullLogger<MeshGossipCertificateProvider>.Instance);

        Should.Throw<InvalidOperationException>(() => provider.GetCertificate())
            .Message.ShouldContain("Base64");
    }

    [Fact]
    public void GetCertificate_ReloadsWhenFileTimestampChanges()
    {
        using var tempDir = new TempDirectory();
        var certificatePath = Path.Combine(tempDir.Path, "mesh-cert.pfx");
        const string password = "mesh-secret";

        var firstBytes = CreateCertificateBytes("CN=mesh-first", password);
        File.WriteAllBytes(certificatePath, firstBytes);

        var options = CreateOptions();
        options.Tls.CertificatePath = certificatePath;
        options.Tls.CertificatePassword = password;

        using var provider = new MeshGossipCertificateProvider(options, NullLogger<MeshGossipCertificateProvider>.Instance);
        using var first = provider.GetCertificate();
        var firstThumbprint = first.Thumbprint;

        var secondBytes = CreateCertificateBytes("CN=mesh-second", password);
        File.WriteAllBytes(certificatePath, secondBytes);
        File.SetLastWriteTimeUtc(certificatePath, DateTime.UtcNow.AddSeconds(1));

        using var second = provider.GetCertificate();
        second.Thumbprint.ShouldNotBe(firstThumbprint);
    }

    [Fact]
    public void GetCertificate_ZeroesInlineDataAfterLoading()
    {
        var options = CreateOptions();
        const string password = "inline-secret";
        var bytes = CreateCertificateBytes("CN=mesh-inline", password);
        options.Tls.CertificatePassword = password;
        options.Tls.CertificateData = Convert.ToBase64String(bytes);

        byte[]? observed = null;
        MeshGossipCertificateProviderTestHooks.SecretsCleared = buffer => observed = buffer.ToArray();

        try
        {
            using var provider = new MeshGossipCertificateProvider(options, NullLogger<MeshGossipCertificateProvider>.Instance);
            using var certificate = provider.GetCertificate();
            certificate.Subject.ShouldContain("mesh-inline");
        }
        finally
        {
            MeshGossipCertificateProviderTestHooks.SecretsCleared = null;
        }

        observed.ShouldNotBeNull();
        observed!.ShouldAllBe(b => b == 0);
    }

    private static MeshGossipOptions CreateOptions() => new()
    {
        NodeId = "test-node",
        ClusterId = "cluster-a",
        Region = "test",
        MeshVersion = "test"
    };

    private static byte[] CreateCertificateBytes(string subjectName, string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.Export(X509ContentType.Pfx, password);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mesh-gossip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }
}
