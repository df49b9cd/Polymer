using System.Net;
using System.Net.Mime;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public sealed class HttpTransportNegotiationTests(ITestOutputHelper output) : IntegrationTest(output)
{
    [Fact(Timeout = 30_000)]
    public async ValueTask HttpInbound_WithHttps_AcceptsHttp11()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http11");

        var dispatcher = CreateDispatcher(
            "http11-service",
            baseAddress,
            new HttpServerRuntimeOptions { EnableHttp3 = false },
            new HttpServerTlsOptions { Certificate = certificate });

        var ct = TestContext.Current.CancellationToken;
        await using var host = await DispatcherHost.StartAsync("http11-service", dispatcher, LoggerFactory, ct);
        await WaitForEndpointReadyAsync(baseAddress, ct);

        using var handler = CreateHttp11Handler();
        using var client = new HttpClient(handler);
        client.BaseAddress = baseAddress;
        client.DefaultRequestVersion = HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;

        client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "protocol::ping");
        client.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");

        using var content = new ByteArrayContent([]);
        var response = await client.PostAsync("/", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Version.Major.ShouldBe(1);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldBe("pong");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask HttpInbound_WithHttps_NegotiatesHttp2()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http2");

        var dispatcher = CreateDispatcher(
            "http2-service",
            baseAddress,
            new HttpServerRuntimeOptions { EnableHttp3 = false },
            new HttpServerTlsOptions { Certificate = certificate });

        var ct = TestContext.Current.CancellationToken;
        await using var host = await DispatcherHost.StartAsync("http2-service", dispatcher, LoggerFactory, ct);
        await WaitForEndpointReadyAsync(baseAddress, ct);

        using var handler = CreateHttp2Handler();
        using var client = new HttpClient(handler) { BaseAddress = baseAddress };
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "protocol::ping");
        client.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");

        using var content = new ByteArrayContent([]);
        var response = await client.PostAsync("/", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Version.Major.ShouldBe(2);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldBe("pong");
    }

    [Http3Fact(Timeout = 30_000)]
    public async ValueTask HttpInbound_WithHttp3_AdvertisesAltSvc()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3");

        var dispatcher = CreateDispatcher(
            "http3-service",
            baseAddress,
            new HttpServerRuntimeOptions
            {
                EnableHttp3 = true,
                Http3 = new Http3RuntimeOptions { EnableAltSvc = true }
            },
            new HttpServerTlsOptions { Certificate = certificate });

        var ct = TestContext.Current.CancellationToken;
        await using var host = await DispatcherHost.StartAsync("http3-service", dispatcher, LoggerFactory, ct);
        await WaitForEndpointReadyAsync(baseAddress, ct);

        using var handler = CreateHttp3Handler();
        using var client = new HttpClient(handler);
        client.BaseAddress = baseAddress;
        client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "protocol::ping");
        client.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;

        var response = await client.PostAsync("/", new ByteArrayContent([]), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Version.Major.ShouldBe(2);
        response.Headers.TryGetValues("Alt-Svc", out var altSvcValues).ShouldBeTrue();
        altSvcValues.ShouldNotBeNull();
        altSvcValues!.ShouldContain(value => value.Contains("h3=\"", StringComparison.OrdinalIgnoreCase));

        var response2 = await client.PostAsync("/", new ByteArrayContent([]), ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
        response2.Version.Major.ShouldBe(3);
    }

    [Http3Fact(Timeout = 30_000)]
    public async ValueTask HttpInbound_WithHttp3Disabled_FallsBackToHttp2()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-fallback");

        var dispatcher = CreateDispatcher(
            "http3-fallback",
            baseAddress,
            new HttpServerRuntimeOptions { EnableHttp3 = false },
            new HttpServerTlsOptions { Certificate = certificate });

        var ct = TestContext.Current.CancellationToken;
        await using var host = await DispatcherHost.StartAsync("http3-fallback", dispatcher, LoggerFactory, ct);
        await WaitForEndpointReadyAsync(baseAddress, ct);

        using var handler = CreateHttp3Handler();
        using var client = new HttpClient(handler) { BaseAddress = baseAddress };
        client.DefaultRequestVersion = HttpVersion.Version30;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "protocol::ping");
        client.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");

        using var content = new ByteArrayContent([]);
        var response = await client.PostAsync("/", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Version.Major.ShouldBe(2);
    }

    private static OmniRelay.Dispatcher.Dispatcher CreateDispatcher(
        string serviceName,
        Uri baseAddress,
        HttpServerRuntimeOptions runtimeOptions,
        HttpServerTlsOptions tlsOptions)
    {
        var options = new DispatcherOptions(serviceName);
        var inbound = HttpInbound.TryCreate([baseAddress], serverRuntimeOptions: runtimeOptions, serverTlsOptions: tlsOptions).ValueOrChecked();
        options.AddLifecycle($"{serviceName}-https", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            serviceName,
            "protocol::ping",
            static (_, _) =>
            {
                var payload = "pong"u8.ToArray();
                var meta = new ResponseMeta(encoding: MediaTypeNames.Text.Plain);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, meta)));
            }));

        return dispatcher;
    }

    private static async Task WaitForEndpointReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port).WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                await Task.Delay(50, cancellationToken);
                return;
            }
            catch
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new TimeoutException($"Endpoint {address} did not accept connections within {maxAttempts} attempts.");
    }

    private static HttpClientHandler CreateHttp11Handler() => new()
    {
        AllowAutoRedirect = false,
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
    };

    private static SocketsHttpHandler CreateHttp2Handler() => new()
    {
        AllowAutoRedirect = false,
        EnableMultipleHttp2Connections = true,
        SslOptions =
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols =
            [
                SslApplicationProtocol.Http2,
                SslApplicationProtocol.Http11
            ]
        }
    };

    private static SocketsHttpHandler CreateHttp3Handler() => new()
    {
        AllowAutoRedirect = false,
        EnableMultipleHttp3Connections = true,
        SslOptions =
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            EnabledSslProtocols = SslProtocols.Tls13,
            ApplicationProtocols =
            [
                SslApplicationProtocol.Http3,
                SslApplicationProtocol.Http2,
                SslApplicationProtocol.Http11
            ]
        }
    };
}
