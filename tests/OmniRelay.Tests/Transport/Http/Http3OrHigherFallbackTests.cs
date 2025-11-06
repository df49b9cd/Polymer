using System;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class Http3OrHigherFallbackTests
{
    [Fact(Timeout = 45_000)]
    public async Task HttpInbound_WithHttp3Enabled_RequestVersionOrHigher_UpgradesToHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSigned("CN=omnirelay-http3-orhigher");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = true };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-orhigher");
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-orhigher",
            "ping",
            (req, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        try
        {
            using var handler = CreateH3CapableHandler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/");
            // Preferred pattern: 1.1 + RequestVersionOrHigher enables HTTP/3 upgrade
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            request.Headers.Add(HttpTransportHeaders.Procedure, "ping");
            request.Content = new ByteArrayContent(Array.Empty<byte>());

            using var response = await client.SendAsync(request, ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(3, response.Version.Major);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task HttpInbound_WithHttp3Disabled_RequestVersionOrHigher_FallsBackToHttp2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSigned("CN=omnirelay-http3-orhigher-fallback");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = false };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-orhigher-fallback");
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-orhigher-fallback",
            "ping",
            (req, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        try
        {
            using var handler = CreateH3CapableHandler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/");
            // Prefer upgrade; fallback if H3 disabled
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            request.Headers.Add(HttpTransportHeaders.Procedure, "ping");
            request.Content = new ByteArrayContent(Array.Empty<byte>());

            using var response = await client.SendAsync(request, ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, response.Version.Major);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    private static SslClientAuthenticationOptions CreateSslOptions()
    {
        var options = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };

        // ApplicationProtocols collection is initialized by SslClientAuthenticationOptions
        _ = options.ApplicationProtocols ?? throw new InvalidOperationException("ApplicationProtocols collection was null.");
        options.ApplicationProtocols!.Add(SslApplicationProtocol.Http3);
        options.ApplicationProtocols!.Add(SslApplicationProtocol.Http2);
        options.ApplicationProtocols!.Add(SslApplicationProtocol.Http11);
        return options;
    }

    private static SocketsHttpHandler CreateH3CapableHandler() => new()
    {
        AllowAutoRedirect = false,
        EnableMultipleHttp2Connections = true,
        EnableMultipleHttp3Connections = true,
        SslOptions = CreateSslOptions()
    };

    private static X509Certificate2 CreateSelfSigned(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(sanBuilder.Build());

        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
