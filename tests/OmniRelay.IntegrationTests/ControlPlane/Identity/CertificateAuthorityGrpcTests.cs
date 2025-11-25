using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Identity;
using OmniRelay.Protos.Ca;
using OmniRelay.TestSupport.Assertions;
using Xunit;

namespace OmniRelay.IntegrationTests.ControlPlane.Identity;

public sealed class CertificateAuthorityGrpcTests : IAsyncLifetime
{
    private WebApplication? _app;
    private GrpcChannel? _channel;
    private CertificateAuthority.CertificateAuthorityClient? _client;
    private string? _rootPath;
    private string? _rootPassword;

    [Fact(Timeout = 30_000)]
    public async Task SubmitCsr_ReturnsRenewalFieldsOverGrpc()
    {
        var csr = CreateCsr("grpc-agent", "spiffe://omnirelay.mesh/mesh/default/grpc-agent");
        var response = await _client!.SubmitCsrAsync(
            new CsrRequest
            {
                NodeId = "grpc-agent",
                Csr = Google.Protobuf.ByteString.CopyFrom(csr)
            }, cancellationToken: TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
        response.IssuedAt.ShouldNotBeNullOrWhiteSpace();
        response.RenewAfter.ShouldNotBeNullOrWhiteSpace();
        response.SanDns.ShouldContain("grpc-agent");
    }

    [Fact(Timeout = 30_000)]
    public async Task RootRotation_UpdatesTrustBundleOverGrpc()
    {
        var csr = CreateCsr("rotate-agent", null);
        var first = await _client!.SubmitCsrAsync(new CsrRequest
        {
            NodeId = "rotate-agent",
            Csr = Google.Protobuf.ByteString.CopyFrom(csr)
        }, cancellationToken: TestContext.Current.CancellationToken);

        await Task.Delay(250, TestContext.Current.CancellationToken); // give filesystem time to settle
        var nextRoot = CreateRootPfx("CN=Rotation-Root-2", _rootPassword!);
        File.WriteAllBytes(_rootPath!, nextRoot);
        await Task.Delay(300, TestContext.Current.CancellationToken); // exceed reload interval

        var second = await _client.SubmitCsrAsync(new CsrRequest
        {
            NodeId = "rotate-agent",
            Csr = Google.Protobuf.ByteString.CopyFrom(csr)
        }, cancellationToken: TestContext.Current.CancellationToken);

        second.TrustBundle.ToByteArray().ShouldNotBe(first.TrustBundle.ToByteArray());
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        _rootPath = Path.Combine(Path.GetTempPath(), $"omnirelay-ca-int-{Guid.NewGuid():N}.pfx");
        _rootPassword = "rotate-pass";
        var initialRoot = CreateRootPfx("CN=Rotation-Root-1", _rootPassword);
        File.WriteAllBytes(_rootPath, initialRoot);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc();
        builder.Services.AddCertificateAuthority(options =>
        {
            options.TrustDomain = "spiffe://omnirelay.mesh";
            options.LeafLifetime = TimeSpan.FromMinutes(20);
            options.RenewalWindow = 0.5;
            options.RootPfxPath = _rootPath;
            options.RootPfxPassword = _rootPassword;
            options.RootReloadInterval = TimeSpan.FromMilliseconds(200);
        });

        var app = builder.Build();
        app.MapGrpcService<CertificateAuthorityService>();
        await app.StartAsync();

        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        var address = app.Urls.Single(url => url.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        var channelOptions = new GrpcChannelOptions();
        _channel = GrpcChannel.ForAddress(address, channelOptions);
        _client = new CertificateAuthority.CertificateAuthorityClient(_channel);
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.ShutdownAsync();
        }

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_rootPath is not null && File.Exists(_rootPath))
        {
            File.Delete(_rootPath);
        }
    }

    private static byte[] CreateCsr(string nodeId, string? uri)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={nodeId}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(nodeId);
        if (!string.IsNullOrWhiteSpace(uri))
        {
            san.AddUri(new Uri(uri));
        }
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSigningRequest();
    }

    private static byte[] CreateRootPfx(string subject, string password)
    {
        using var key = RSA.Create(3072);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = req.CreateSelfSigned(now.UtcDateTime, now.AddDays(30).UtcDateTime);
        return root.Export(X509ContentType.Pfx, password);
    }
}
