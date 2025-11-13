using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Tests.Support;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Transport.Http;

public class HttpOutboundVersionPolicyTests
{
    [Http3Fact(Timeout = 45_000)]
    public async Task HttpOutbound_WithHttp3Enabled_UsesHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-outbound");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = true };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-outbound");
        var inbound = new HttpInbound([address.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-outbound-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-outbound",
            "http3-outbound::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForHttpReadyAsync(address, ct);

        using var handler = CreateHttp3SocketsHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var outbound = new HttpOutbound(httpClient, address, disposeClient: false, runtimeOptions: new HttpClientRuntimeOptions
        {
            EnableHttp3 = true,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

        try
        {
            await outbound.StartAsync(ct);
            var meta = new RequestMeta("http3-outbound", "http3-outbound::ping", encoding: RawCodec.DefaultEncoding);
            var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
            var result = await ((IUnaryOutbound)outbound).CallAsync(request, ct);
            result.IsSuccess.ShouldBeTrue();

            var protocol = result.Value.Meta.Headers.FirstOrDefault(h => string.Equals(h.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase)).Value;
            protocol.ShouldNotBeNull();
            protocol.ShouldStartWith("HTTP/3");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Http3Fact(Timeout = 45_000)]
    public async Task HttpOutbound_WithOrHigher_ToHttp2Server_DowngradesToHttp2()
    {
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http2-outbound");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = false };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http2-outbound");
        var inbound = new HttpInbound([address.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http2-outbound-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http2-outbound",
            "http2-outbound::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForHttpReadyAsync(address, ct);

        using var handler = CreateHttp3SocketsHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var outbound = new HttpOutbound(httpClient, address, disposeClient: false, runtimeOptions: new HttpClientRuntimeOptions
        {
            EnableHttp3 = true,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        });

        try
        {
            await outbound.StartAsync(ct);
            var meta = new RequestMeta("http2-outbound", "http2-outbound::ping", encoding: RawCodec.DefaultEncoding);
            var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
            var result = await ((IUnaryOutbound)outbound).CallAsync(request, ct);
            result.IsSuccess.ShouldBeTrue();

            var protocol = result.Value.Meta.Headers.FirstOrDefault(h => string.Equals(h.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase)).Value;
            protocol.ShouldNotBeNull();
            protocol.ShouldStartWith("HTTP/2");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Http3Fact(Timeout = 45_000)]
    public async Task HttpOutbound_WithExactHttp3_ToHttp2Server_Fails()
    {
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-exact-outbound");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = false };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-exact-outbound");
        var inbound = new HttpInbound([address.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-exact-outbound-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-exact-outbound",
            "http3-exact-outbound::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForHttpReadyAsync(address, ct);

        using var handler = CreateHttp3SocketsHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var outbound = new HttpOutbound(httpClient, address, disposeClient: false, runtimeOptions: new HttpClientRuntimeOptions
        {
            EnableHttp3 = true,
            RequestVersion = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

        try
        {
            await outbound.StartAsync(ct);
            var meta = new RequestMeta("http3-exact-outbound", "http3-exact-outbound::ping", encoding: RawCodec.DefaultEncoding);
            var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
            var result = await ((IUnaryOutbound)outbound).CallAsync(request, ct);
            result.IsFailure.ShouldBeTrue("Call should fail when HTTP/3 exact is required but server is HTTP/2 only.");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    private static async Task WaitForHttpReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        const int connectTimeoutMilliseconds = 200;
        const int settleDelayMilliseconds = 50;
        const int retryDelayMilliseconds = 20;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(address.Host, address.Port)
                            .WaitAsync(TimeSpan.FromMilliseconds(connectTimeoutMilliseconds), cancellationToken);

                await Task.Delay(TimeSpan.FromMilliseconds(settleDelayMilliseconds), cancellationToken);
                return;
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
        }

        throw new TimeoutException("The HTTP inbound failed to bind within the allotted time.");
    }

    private static SocketsHttpHandler CreateHttp3SocketsHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols =
                [
                    SslApplicationProtocol.Http3,
                    SslApplicationProtocol.Http2
                ]
            }
        };

        return handler;
    }

}
