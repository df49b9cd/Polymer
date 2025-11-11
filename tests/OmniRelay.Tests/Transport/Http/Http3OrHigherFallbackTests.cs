using System;
using System.Collections.Generic;
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
using OmniRelay.Tests.Support;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class Http3OrHigherFallbackTests
{
    [Http3Fact(Timeout = 45_000)]
    public async Task HttpInbound_WithHttp3Enabled_RequestVersionOrHigher_UpgradesToHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-orhigher");

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
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var handler = CreateH3CapableHandler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
            // Explicitly request HTTP/3 for the first call
            client.DefaultRequestVersion = HttpVersion.Version30;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "ping");

            using var response = await client.PostAsync("/", new ByteArrayContent([]), ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(3, response.Version.Major);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Http3Fact(Timeout = 45_000)]
    public async Task HttpInbound_WithHttp3Disabled_RequestVersionOrHigher_FallsBackToHttp2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-orhigher-fallback");

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
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var handler = CreateH3CapableHandler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
            // Prefer HTTP/3 but allow downgrade when the server disables it
            client.DefaultRequestVersion = HttpVersion.Version30;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "ping");

            using var response = await client.PostAsync("/", new ByteArrayContent([]), ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, response.Version.Major);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    private static SslClientAuthenticationOptions CreateSslOptions()
    {
        var options = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols =
            [
                SslApplicationProtocol.Http3,
                SslApplicationProtocol.Http2
            ]
        };

        return options;
    }

    private static SocketsHttpHandler CreateH3CapableHandler() => new()
    {
        AllowAutoRedirect = false,
        EnableMultipleHttp2Connections = true,
        EnableMultipleHttp3Connections = true,
        SslOptions = CreateSslOptions()
    };

}
