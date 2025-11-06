using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Transport;

public class HttpInboundLifecycleTests
{
    [Fact(Timeout = 30_000)]
    public async Task StopAsync_WaitsForActiveRequestsAndRejectsNewOnes()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("lifecycle");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "lifecycle",
            "test::slow",
            async (request, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };

        using var initialRequest = CreateRpcRequest("test::slow");
        var inFlightTask = httpClient.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsync(ct);

        await Task.Delay(100, ct);

        using var rejectedRequest = CreateRpcRequest("test::slow");
        using var rejectedResponse = await httpClient.SendAsync(rejectedRequest, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedResponse.StatusCode);
        Assert.True(rejectedResponse.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Contains("1", retryAfterValues);
        Assert.False(stopTask.IsCompleted);

        releaseRequest.TrySetResult();

        using var response = await inFlightTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await stopTask;
    }

    [Fact(Timeout = 45_000)]
    public async Task StopAsync_WithHttp3Request_PropagatesRetryAfter()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-http3-lifecycle");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = true };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("lifecycle-http3");
        var httpInbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http-inbound-http3", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "lifecycle-http3",
            "test::slow",
            async (request, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var handler = CreateHttp3Handler();
        using var httpClient = new HttpClient(handler) { BaseAddress = baseAddress };

        using var initialRequest = CreateRpcRequest("test::slow");
        initialRequest.Version = HttpVersion.Version30;
        initialRequest.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        var inFlightTask = httpClient.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsync(ct);
        await Task.Delay(100, ct);

        using var rejectedRequest = CreateRpcRequest("test::slow");
        rejectedRequest.Version = HttpVersion.Version30;
        rejectedRequest.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        using var rejectedResponse = await httpClient.SendAsync(rejectedRequest, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedResponse.StatusCode);
        Assert.True(rejectedResponse.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Contains("1", retryAfterValues);
        Assert.Equal(3, rejectedResponse.Version.Major);
        Assert.False(stopTask.IsCompleted);

        releaseRequest.TrySetResult();

        using var response = await inFlightTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, response.Version.Major);

        await stopTask;
    }

    [Fact(Timeout = 45_000)]
    public async Task StopAsync_WithHttp3Fallback_PropagatesRetryAfter()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-http3-fallback-drain");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = false };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("lifecycle-http3-fallback");
        var httpInbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http-inbound-http3-fallback", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "lifecycle-http3-fallback",
            "test::slow",
            async (request, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var handler = CreateHttp3Handler();
        using var httpClient = new HttpClient(handler) { BaseAddress = baseAddress };
        httpClient.DefaultRequestVersion = HttpVersion.Version30;
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        using var initialRequest = CreateRpcRequest("test::slow");
        initialRequest.Version = HttpVersion.Version30;
        initialRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        var inFlightTask = httpClient.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsync(ct);
        await Task.Delay(100, ct);

        using var rejectedRequest = CreateRpcRequest("test::slow");
        rejectedRequest.Version = HttpVersion.Version30;
        rejectedRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        using var rejectedResponse = await httpClient.SendAsync(rejectedRequest, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedResponse.StatusCode);
        Assert.True(rejectedResponse.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Contains("1", retryAfterValues);
        Assert.Equal(2, rejectedResponse.Version.Major);
        Assert.True(rejectedResponse.Headers.TryGetValues(HttpTransportHeaders.Protocol, out var protocolValues));
        Assert.Contains("HTTP/2", protocolValues);
        Assert.False(stopTask.IsCompleted);

        releaseRequest.TrySetResult();

        using var response = await inFlightTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Version.Major);
        Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Protocol, out var inflightProtocolValues));
        Assert.Contains("HTTP/2", inflightProtocolValues);

        await stopTask;
    }

    [Fact(Timeout = 45_000)]
    public async Task HttpInbound_WithHttp3_ExposesObservabilityEndpoints()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-http3-observability");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = true };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-observability");
        var httpInbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-observability-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-observability",
            "health::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        try
        {
            using var http3Handler = CreateHttp3Handler();
            using var http3Client = new HttpClient(http3Handler) { BaseAddress = baseAddress };
            http3Client.DefaultRequestVersion = HttpVersion.Version30;
            http3Client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;

            using var introspectResponse = await http3Client.GetAsync("/omnirelay/introspect", ct);
            Assert.Equal(HttpStatusCode.OK, introspectResponse.StatusCode);
            Assert.Equal(3, introspectResponse.Version.Major);
            var payload = await introspectResponse.Content.ReadAsStringAsync(ct);
            using (var document = JsonDocument.Parse(payload))
            {
                Assert.Equal("http3-observability", document.RootElement.GetProperty("service").GetString());
                Assert.Equal("Running", document.RootElement.GetProperty("status").GetString());
            }

            using var healthResponse = await http3Client.GetAsync("/healthz", ct);
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
            Assert.Equal(3, healthResponse.Version.Major);

            using var readyResponse = await http3Client.GetAsync("/readyz", ct);
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
            Assert.Equal(3, readyResponse.Version.Major);

            using var http11Handler = CreateHttp11Handler();
            using var http11Client = new HttpClient(http11Handler) { BaseAddress = baseAddress };

            using var http11Request = new HttpRequestMessage(HttpMethod.Get, "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var http11Response = await http11Client.SendAsync(http11Request, ct);
            Assert.Equal(HttpStatusCode.OK, http11Response.StatusCode);
            Assert.Equal(1, http11Response.Version.Major);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task StopAsync_WithCancellation_CompletesWithoutWaiting()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("lifecycle");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "lifecycle",
            "test::slow",
            async (request, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };

        using var initialRequest = CreateRpcRequest("test::slow");
        var inFlightTask = httpClient.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await requestStarted.Task.WaitAsync(ct);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopTask = dispatcher.StopAsync(stopCts.Token);

        await stopTask;
        releaseRequest.TrySetResult();

        await Assert.ThrowsAnyAsync<Exception>(async () => await inFlightTask);
    }

    [Fact(Timeout = 30_000)]
    public async Task HealthEndpoints_ReflectDispatcherState()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("health");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "health",
            "ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };

        using var healthResponse = await httpClient.GetAsync("/healthz", ct);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        using var readinessResponse = await httpClient.GetAsync("/readyz", ct);
        Assert.Equal(HttpStatusCode.OK, readinessResponse.StatusCode);

        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Register(new UnaryProcedureSpec(
            "health",
            "slow",
            async (request, token) =>
            {
                slowStarted.TrySetResult();
                await release.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        using var slowRequest = CreateRpcRequest("slow");
        var slowTask = httpClient.SendAsync(slowRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await slowStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsync(ct);

        using var drainingReadiness = await httpClient.GetAsync("/readyz", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, drainingReadiness.StatusCode);

        release.TrySetResult();
        using var slowResponse = await slowTask;
        Assert.Equal(HttpStatusCode.OK, slowResponse.StatusCode);

        await stopTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task IntrospectEndpoint_ReturnsDispatcherSnapshot()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("introspect");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "introspect",
            "service::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        using var response = await httpClient.GetAsync("/omnirelay/introspect", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        Assert.Equal("introspect", root.GetProperty("service").GetString());

        var unaryProcedures = root.GetProperty("procedures").GetProperty("unary");
        Assert.Contains(unaryProcedures.EnumerateArray(), element => string.Equals(element.GetProperty("name").GetString(), "service::ping", StringComparison.Ordinal));

        await dispatcher.StopAsync(ct);
    }

    private static HttpRequestMessage CreateRpcRequest(string procedure)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.Add(HttpTransportHeaders.Procedure, procedure);
        request.Headers.Add(HttpTransportHeaders.Transport, "http");
        request.Content = new ByteArrayContent([]);
        return request;
    }

    private static SocketsHttpHandler CreateHttp3Handler()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            EnableMultipleHttp3Connections = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http3,
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11
                }
            }
        };

        return handler;
    }

    private static HttpClientHandler CreateHttp11Handler() => new()
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
    };

    private static X509Certificate2 CreateSelfSignedCertificate(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
