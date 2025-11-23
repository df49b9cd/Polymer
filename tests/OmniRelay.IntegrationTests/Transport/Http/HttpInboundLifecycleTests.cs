using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport;

public sealed class HttpInboundLifecycleTests(ITestOutputHelper output) : TransportIntegrationTest(output)
{
    [Fact(Timeout = 30_000)]
    public async ValueTask StopAsync_WaitsForActiveRequestsAndRejectsNewOnes()
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
        await using var host = await StartDispatcherAsync(nameof(StopAsync_WaitsForActiveRequestsAndRejectsNewOnes), dispatcher, ct, ownsLifetime: false);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "test::slow");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");

        var inFlightTask = httpClient.PostAsync("/", new ByteArrayContent([]), ct);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsyncChecked(ct);

        await Task.Delay(100, ct);

        using var rejectedResponse = await httpClient.PostAsync("/", new ByteArrayContent([]), ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedResponse.StatusCode);
        Assert.True(rejectedResponse.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Contains("1", retryAfterValues);
        Assert.False(stopTask.IsCompleted);

        releaseRequest.TrySetResult();

        using var response = await inFlightTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await stopTask;
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask StopAsync_ResetsDrainSignalAndAllowsRestart()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("lifecycle-restart");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;

        dispatcher.Register(new UnaryProcedureSpec(
            "lifecycle-restart",
            "test::slow",
            async (request, token) =>
            {
                var invocation = Interlocked.Increment(ref requestCount);
                if (invocation == 1)
                {
                    requestStarted.TrySetResult();
                    await releaseRequest.Task.WaitAsync(token);
                }

                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartDispatcherAsync(nameof(StopAsync_ResetsDrainSignalAndAllowsRestart), dispatcher, ct, ownsLifetime: false);
        await WaitForHttpReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "test::slow");

        var inFlightTask = httpClient.PostAsync("/", new ByteArrayContent([]), ct);
        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsyncChecked(ct);
        Assert.False(stopTask.IsCompleted);

        releaseRequest.TrySetResult();

        using (var firstResponse = await inFlightTask)
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }

        await stopTask;

        await dispatcher.StartAsyncChecked(ct);
        await WaitForHttpReadyAsync(baseAddress, ct);

        using (var secondResponse = await httpClient.PostAsync("/", new ByteArrayContent([]), ct))
        {
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        }

        Assert.Equal(2, Volatile.Read(ref requestCount));

        await dispatcher.StopAsyncChecked(ct);
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask StopAsync_WithHttp3Request_PropagatesRetryAfter()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-lifecycle");

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
        await using var host = await StartDispatcherAsync(nameof(StopAsync_WithHttp3Request_PropagatesRetryAfter), dispatcher, ct, ownsLifetime: false);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var handler = CreateHttp3Handler();
        using var httpClient = new HttpClient(handler) { BaseAddress = baseAddress };
        httpClient.DefaultRequestVersion = HttpVersion.Version30;
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "test::slow");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");

        var inFlightTask = httpClient.PostAsync("/", new ByteArrayContent([]), ct);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsyncChecked(ct);
        await Task.Delay(100, ct);

        using var rejectedResponse = await httpClient.PostAsync("/", new ByteArrayContent([]), ct);

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

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask StopAsync_WithHttp3Fallback_PropagatesRetryAfter()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-fallback-drain");

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
        await using var host = await StartDispatcherAsync(nameof(StopAsync_WithHttp3Fallback_PropagatesRetryAfter), dispatcher, ct, ownsLifetime: false);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var handler = CreateHttp3Handler();
        using var httpClient = new HttpClient(handler) { BaseAddress = baseAddress };
        httpClient.DefaultRequestVersion = HttpVersion.Version30;
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "test::slow");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");

        var inFlightTask = httpClient.PostAsync("/", new ByteArrayContent([]), ct);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsyncChecked(ct);
        await Task.Delay(100, ct);

        using var rejectedResponse = await httpClient.PostAsync("/", new ByteArrayContent([]), ct);

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

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask HttpInbound_WithHttp3_ExposesObservabilityEndpoints()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-observability");

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
        await using var host = await StartDispatcherAsync(nameof(HttpInbound_WithHttp3_ExposesObservabilityEndpoints), dispatcher, ct);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

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

        http11Client.DefaultRequestVersion = HttpVersion.Version11;
        http11Client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        using var http11Response = await http11Client.GetAsync("/healthz", ct);
        Assert.Equal(HttpStatusCode.OK, http11Response.StatusCode);
        Assert.Equal(1, http11Response.Version.Major);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask StopAsync_WithCancellation_CompletesWithoutWaiting()
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
        await using var host = await StartDispatcherAsync(nameof(StopAsync_WithCancellation_CompletesWithoutWaiting), dispatcher, ct, ownsLifetime: false);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "test::slow");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");

        var inFlightTask = httpClient.PostAsync("/", new ByteArrayContent([]), ct);

        await requestStarted.Task.WaitAsync(ct);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopResult = await dispatcher.StopAsync(stopCts.Token);
        Assert.True(stopResult.IsSuccess);

        releaseRequest.TrySetResult();

        try
        {
            using var response = await inFlightTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Connection can close while draining; both outcomes are acceptable.
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask HealthEndpoints_ReflectDispatcherState()
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
        await using var host = await StartDispatcherAsync(nameof(HealthEndpoints_ReflectDispatcherState), dispatcher, ct, ownsLifetime: false);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

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

        using var rpcClient = new HttpClient { BaseAddress = baseAddress };
        rpcClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "slow");
        rpcClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Transport, "http");
        var slowTask = rpcClient.PostAsync("/", new ByteArrayContent([]), ct);

        await slowStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsyncChecked(ct);

        using var drainingReadiness = await httpClient.GetAsync("/readyz", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, drainingReadiness.StatusCode);

        release.TrySetResult();
        using var slowResponse = await slowTask;
        Assert.Equal(HttpStatusCode.OK, slowResponse.StatusCode);

        await stopTask;
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask IntrospectEndpoint_ReturnsDispatcherSnapshot()
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
        await using var host = await StartDispatcherAsync(nameof(IntrospectEndpoint_ReturnsDispatcherSnapshot), dispatcher, ct);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        using var response = await httpClient.GetAsync("/omnirelay/introspect", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        Assert.Equal("introspect", root.GetProperty("service").GetString());

        var unaryProcedures = root.GetProperty("procedures").GetProperty("unary");
        Assert.Contains(unaryProcedures.EnumerateArray(), element => string.Equals(element.GetProperty("name").GetString(), "service::ping", StringComparison.Ordinal));
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
                ApplicationProtocols =
                [
                    SslApplicationProtocol.Http3,
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11
                ]
            }
        };

        return handler;
    }

    private static HttpClientHandler CreateHttp11Handler() => new()
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
    };

    private static Task WaitForHttpReadyAsync(Uri address, CancellationToken cancellationToken) =>
        WaitForHttpEndpointReadyAsync(address, cancellationToken);
}
