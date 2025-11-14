using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Security;
using Shouldly;
using Xunit;

namespace OmniRelay.Core.UnitTests.ControlPlane;

public sealed class TransportTlsManagerTests
{
    [Fact]
    public void GetCertificate_ThrowsWhenCertificatePathMissing()
    {
        var options = CreateOptions();
        options.CertificatePath = null;
        options.CertificateData = null;

        using var manager = new TransportTlsManager(options, NullLogger<TransportTlsManager>.Instance);

        Should.Throw<InvalidOperationException>(() => manager.GetCertificate())
            .Message.ShouldContain("certificate path");
    }

    [Fact]
    public void GetCertificate_ThrowsWhenInlineCertificateDataIsInvalid()
    {
        var options = CreateOptions();
        options.CertificateData = "not-base64!";
        options.CertificatePassword = "pass";

        using var manager = new TransportTlsManager(options, NullLogger<TransportTlsManager>.Instance);

        Should.Throw<InvalidOperationException>(() => manager.GetCertificate())
            .Message.ShouldContain("Base64");
    }

    [Fact]
    public void GetCertificate_ReloadsWhenFileTimestampChanges()
    {
        using var tempDir = new TempDirectory();
        var certificatePath = Path.Combine(tempDir.Path, "transport-cert.pfx");
        const string password = "mesh-secret";

        var firstBytes = CreateCertificateBytes("CN=transport-first", password);
        File.WriteAllBytes(certificatePath, firstBytes);

        var options = CreateOptions();
        options.CertificatePath = certificatePath;
        options.CertificatePassword = password;

        using var manager = new TransportTlsManager(options, NullLogger<TransportTlsManager>.Instance);
        using var first = manager.GetCertificate();
        var firstThumbprint = first.Thumbprint;

        var secondBytes = CreateCertificateBytes("CN=transport-second", password);
        File.WriteAllBytes(certificatePath, secondBytes);
        File.SetLastWriteTimeUtc(certificatePath, DateTime.UtcNow.AddSeconds(1));

        using var second = manager.GetCertificate();
        second.Thumbprint.ShouldNotBe(firstThumbprint);
    }

    [Fact]
    public void GetCertificate_ZeroesInlineDataAfterLoading()
    {
        var options = CreateOptions();
        const string password = "inline-secret";
        var bytes = CreateCertificateBytes("CN=transport-inline", password);
        options.CertificatePassword = password;
        options.CertificateData = Convert.ToBase64String(bytes);

        byte[]? observed = null;
        TransportTlsManagerTestHooks.SecretsCleared = buffer => observed = buffer.ToArray();

        try
        {
            using var manager = new TransportTlsManager(options, NullLogger<TransportTlsManager>.Instance);
            using var certificate = manager.GetCertificate();
            certificate.Subject.ShouldContain("transport-inline");
        }
        finally
        {
            TransportTlsManagerTestHooks.SecretsCleared = null;
        }

        observed.ShouldNotBeNull();
        observed!.ShouldAllBe(b => b == 0);
    }

    private static TransportTlsOptions CreateOptions() => new()
    {
        CertificatePath = Path.Combine(Path.GetTempPath(), $"omnirelay-transport-{Guid.NewGuid():N}.pfx"),
        ReloadInterval = TimeSpan.FromSeconds(1)
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"transport-tls-{Guid.NewGuid():N}");
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
