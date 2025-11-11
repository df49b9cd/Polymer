using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Tests.Support;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class Http3NegotiationTests
{
    [Http3Fact(Timeout = 45000)]
    public async Task HttpInbound_WithHttp3Enabled_AllowsHttp3Requests()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = true };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-enabled");
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-enabled",
            "ping",
            (req, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var handler = CreateHttp3Handler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
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
    public async Task HttpInbound_WithHttp3_ServerStreamHandlesLargePayload()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-stream");

        var payload = new byte[512 * 1024];
        RandomNumberGenerator.Fill(payload);

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = true };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-stream");
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-stream-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new StreamProcedureSpec(
            "http3-stream",
            "http3-stream::tail",
            async (request, streamOptions, cancellationToken) =>
            {
                var call = HttpStreamCall.CreateServerStream(request.Meta, new ResponseMeta(encoding: "application/octet-stream"));
                await call.WriteAsync(payload, cancellationToken);
                await call.CompleteAsync(cancellationToken: cancellationToken);
                return Hugo.Go.Ok<IStreamCall>(call);
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var handler = CreateHttp3Handler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
            client.DefaultRequestVersion = HttpVersion.Version30;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "http3-stream::tail");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

            using var response = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(3, response.Version.Major);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? dataLine = null;
            string? encodingLine = null;

            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    dataLine = line[6..];
                }
                else if (line.StartsWith("encoding: ", StringComparison.Ordinal))
                {
                    encodingLine = line[10..];
                }
                else if (line.Length == 0 && dataLine is not null)
                {
                    break;
                }
            }

            Assert.NotNull(dataLine);
            Assert.Equal("base64", encodingLine);

            var decoded = Convert.FromBase64String(dataLine!);
            Assert.Equal(payload.Length, decoded.Length);
            Assert.True(decoded.AsSpan().SequenceEqual(payload));
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Http3Fact(Timeout = 45000)]
    public async Task HttpInbound_WithHttp3Enabled_AllowsHttp1Requests()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = true };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-http1");
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-http1",
            "ping",
            (req, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var handler = CreateHttp11Handler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
            client.DefaultRequestVersion = HttpVersion.Version11;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "ping");

            using var response = await client.PostAsync("/", new ByteArrayContent([]), ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, response.Version.Major);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Http3Fact(Timeout = 45000)]
    public async Task HttpInbound_WithHttp3Disabled_FallsBackToHttp2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = false };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-disabled");
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-disabled",
            "ping",
            (req, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var handler = CreateHttp3Handler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
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

    private static SocketsHttpHandler CreateHttp3Handler() => new()
    {
        AllowAutoRedirect = false,
        SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols =
                [
                    SslApplicationProtocol.Http3,
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11
                ]
            }
    };

    private static HttpClientHandler CreateHttp11Handler() => new()
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };

}
