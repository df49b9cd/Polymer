using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using Grpc.Core;
using Grpc.Net.Client;
using Hugo.Policies;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class ResiliencyIntegrationTests
{
    [Fact(Timeout = 60_000)]
    public async Task HttpInbound_StopAsync_DrainsAndRejectsNewRequests()
    {
        var httpPort = TestPortAllocator.GetRandomPort();
        var httpBase = new Uri($"http://127.0.0.1:{httpPort}/");

        var options = new DispatcherOptions("resiliency-http-drain");
        var httpInbound = new HttpInbound([httpBase.ToString()]);
        options.AddLifecycle("resiliency-http-drain-inbound", httpInbound);

        var dispatcher = new Dispatcher.Dispatcher(options);

        var startedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        dispatcher.Register(new UnaryProcedureSpec(
            "resiliency-http-drain",
            "resiliency::slow",
            async (request, token) =>
            {
                if (Interlocked.Increment(ref startedCount) >= 1)
                {
                    startedSignal.TrySetResult();
                }

                await releaseSignal.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForHttpReadyAsync(httpBase, ct);

        using var httpClient = new HttpClient { BaseAddress = httpBase };
        using var initialHttpRequest = CreateHttpRequest("resiliency::slow");
        var inflight = httpClient.SendAsync(initialHttpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await startedSignal.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopOrThrowAsync(ct);

        await Task.Delay(100, ct);

        using (var rejectedRequest = CreateHttpRequest("resiliency::slow"))
        using (var rejectedResponse = await httpClient.SendAsync(rejectedRequest, ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedResponse.StatusCode);
            AssertRetryAfter(rejectedResponse.Headers);
            await rejectedResponse.Content.ReadAsByteArrayAsync(ct);
        }

        Assert.False(stopTask.IsCompleted);

        releaseSignal.TrySetResult();

        using (var response = await inflight)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsByteArrayAsync(ct);
        }

        await stopTask;
    }

    [Fact(Timeout = 60_000)]
    public async Task GrpcInbound_StopAsync_DrainsAndRejectsNewCalls()
    {
        var grpcPort = TestPortAllocator.GetRandomPort();
        var grpcAddress = new Uri($"http://127.0.0.1:{grpcPort}");

        var options = new DispatcherOptions("resiliency-grpc-drain");
        var grpcInbound = new GrpcInbound([grpcAddress.ToString()]);
        options.AddLifecycle("resiliency-grpc-drain-inbound", grpcInbound);

        var dispatcher = new Dispatcher.Dispatcher(options);
        var startedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "resiliency-grpc-drain",
            "resiliency::slow",
            async (request, token) =>
            {
                startedSignal.TrySetResult();
                await releaseSignal.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(grpcAddress, ct);

        using var channel = GrpcChannel.ForAddress(grpcAddress);
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(
            MethodType.Unary,
            "resiliency-grpc-drain",
            "resiliency::slow",
            ByteMarshaller,
            ByteMarshaller);

        using var inflight = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);

        await startedSignal.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopOrThrowAsync(ct);
        await Task.Delay(100, ct);

        var rejectedCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
        var rejection = await Assert.ThrowsAsync<RpcException>(() => rejectedCall.ResponseAsync);
        rejectedCall.Dispose();
        Assert.Equal(StatusCode.Unavailable, rejection.StatusCode);
        Assert.Equal("1", rejection.Trailers.GetValue("retry-after"));

        Assert.False(stopTask.IsCompleted);

        releaseSignal.TrySetResult();

        var inflightResponse = await inflight.ResponseAsync.WaitAsync(ct);
        Assert.Empty(inflightResponse);

        await stopTask;
    }

    [Fact(Timeout = 60_000)]
    public async Task GrpcOutbound_WithMutualTlsRequirement_SurfacesRetryableMetadata()
    {
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-resiliency-handshake");

        var backendPort = TestPortAllocator.GetRandomPort();
        var backendAddress = new Uri($"https://127.0.0.1:{backendPort}");
        var backendOptions = new DispatcherOptions("resiliency-handshake-backend");
        var backendInbound = new GrpcInbound([backendAddress.ToString()], serverTlsOptions: new GrpcServerTlsOptions
        {
            Certificate = certificate,
            ClientCertificateMode = ClientCertificateMode.RequireCertificate,
            ClientCertificateValidation = static (_, _, _) => true
        });
        backendOptions.AddLifecycle("resiliency-handshake-grpc", backendInbound);

        var backendDispatcher = new Dispatcher.Dispatcher(backendOptions);
        backendDispatcher.Register(new UnaryProcedureSpec(
            "resiliency-handshake-backend",
            "resiliency-backend::echo",
            (request, _) =>
            {
                var meta = new ResponseMeta(encoding: RawCodec.DefaultEncoding);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(request.Body, meta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await backendDispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(backendAddress, ct);

        var outbound = new GrpcOutbound(
            backendAddress,
            remoteService: "resiliency-handshake-backend",
            clientTlsOptions: new GrpcClientTlsOptions
            {
                ServerCertificateValidationCallback = static (_, _, _, _) => true
            });

        await outbound.StartAsync(ct);

        try
        {
            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, []);
            var request = new Request<byte[]>(
                new RequestMeta("resiliency-handshake-backend", "resiliency-backend::echo", encoding: codec.Encoding, transport: GrpcTransport),
                []);

            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsFailure);

            var error = result.Error!;
            Assert.Equal("unavailable", error.Code);
            Assert.True(error.TryGetMetadata(TransportMetadataKey, out string? transport));
            Assert.Equal(GrpcTransport, transport);
            Assert.True(error.TryGetMetadata(RetryableMetadataKey, out bool retryable));
            Assert.True(retryable);
        }
        finally
        {
            await outbound.StopAsync(CancellationToken.None);
            await backendDispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Http3Fact(Timeout = 90_000)]
    public async Task GrpcClient_WithHttp3Preferred_FallsBackToHttp2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-h3-fallback");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var backendOptions = new DispatcherOptions("resiliency-h3-backend");
        var inbound = new GrpcInbound(
            [address.ToString()],
            serverTlsOptions: new GrpcServerTlsOptions { Certificate = certificate },
            serverRuntimeOptions: new GrpcServerRuntimeOptions { EnableHttp3 = false });
        backendOptions.AddLifecycle("resiliency-h3-grpc", inbound);

        var protocolCapture = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var backendDispatcher = new Dispatcher.Dispatcher(backendOptions);
        backendDispatcher.RegisterUnary("resiliency::h3probe", builder =>
        {
            builder.Handle((request, _) =>
            {
                if (request.Meta.Headers.TryGetValue("rpc.protocol", out var protocol))
                {
                    protocolCapture.TrySetResult(protocol);
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())));
            });
        });

        var clientRuntime = new GrpcClientRuntimeOptions
        {
            EnableHttp3 = true,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        var outbound = new GrpcOutbound(
            address,
            remoteService: "resiliency-h3-backend",
            clientTlsOptions: new GrpcClientTlsOptions
            {
                ServerCertificateValidationCallback = static (_, _, _, _) => true
            },
            clientRuntimeOptions: clientRuntime,
            endpointHttp3Support: new Dictionary<Uri, bool> { [address] = true });

        var codec = new RawCodec();
        var client = new UnaryClient<byte[], byte[]>(outbound, codec, []);

        var ct = TestContext.Current.CancellationToken;
        await backendDispatcher.StartOrThrowAsync(ct);
        await outbound.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var request = new Request<byte[]>(
                new RequestMeta("resiliency-h3-backend", "resiliency::h3probe", encoding: codec.Encoding, transport: GrpcTransport),
                []);

            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsSuccess);

            var observed = await protocolCapture.Task.WaitAsync(ct);
            Assert.Equal("HTTP/2", observed);
        }
        finally
        {
            await outbound.StopAsync(CancellationToken.None);
            await backendDispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DeadlinesAndCancellations_SurfaceStatusesAndRetryHints()
    {
        var timeoutPort = TestPortAllocator.GetRandomPort();
        var timeoutBase = new Uri($"http://127.0.0.1:{timeoutPort}/");

        var timeoutOptions = new DispatcherOptions("resiliency-deadlines");
        var timeoutInbound = new HttpInbound([timeoutBase.ToString()]);
        timeoutOptions.AddLifecycle("resiliency-deadlines-http", timeoutInbound);
        timeoutOptions.UnaryInboundMiddleware.Add(new DeadlineMiddleware());

        var timeoutDispatcher = new Dispatcher.Dispatcher(timeoutOptions);
        timeoutDispatcher.RegisterUnary("resiliency::timeout", builder =>
        {
            builder.Handle(async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            });
        });

        var cancelPort = TestPortAllocator.GetRandomPort();
        var cancelAddress = new Uri($"http://127.0.0.1:{cancelPort}");

        var cancelOptions = new DispatcherOptions("resiliency-cancel");
        var cancelInbound = new GrpcInbound([cancelAddress.ToString()]);
        cancelOptions.AddLifecycle("resiliency-cancel-grpc", cancelInbound);

        var cancelDispatcher = new Dispatcher.Dispatcher(cancelOptions);
        cancelDispatcher.RegisterUnary("resiliency::cancel", builder =>
        {
            builder.Handle(async (request, token) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    var error = OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.Cancelled,
                        "call cancelled by caller",
                        transport: request.Meta.Transport ?? GrpcTransport);
                    return Err<Response<ReadOnlyMemory<byte>>>(error);
                }

                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            });
        });

        var ct = TestContext.Current.CancellationToken;
        await timeoutDispatcher.StartOrThrowAsync(ct);
        await cancelDispatcher.StartOrThrowAsync(ct);
        await WaitForHttpReadyAsync(timeoutBase, ct);
        await WaitForGrpcReadyAsync(cancelAddress, ct);

        try
        {
            using (var httpClient = new HttpClient { BaseAddress = timeoutBase })
            using (var request = CreateHttpRequest("resiliency::timeout", ttlMs: 50))
            using (var response = await httpClient.SendAsync(request, ct))
            {
                Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
                AssertHeader(response.Headers, HttpTransportHeaders.Status, OmniRelayStatusCode.DeadlineExceeded.ToString());

                var json = await response.Content.ReadAsStringAsync(ct);
                using var document = JsonDocument.Parse(json);
                var metadata = document.RootElement.GetProperty("metadata");
                Assert.True(metadata.GetProperty(RetryableMetadataKey).GetBoolean());
            }

            using var channel = GrpcChannel.ForAddress(cancelAddress);
            var invoker = channel.CreateCallInvoker();
            var method = new Method<byte[], byte[]>(
                MethodType.Unary,
                "resiliency-cancel",
                "resiliency::cancel",
                ByteMarshaller,
                ByteMarshaller);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var call = invoker.AsyncUnaryCall(method, null, new CallOptions(cancellationToken: cts.Token), []);
            var rpcException = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
            Assert.Equal(StatusCode.Cancelled, rpcException.StatusCode);
        }
        finally
        {
            await cancelDispatcher.StopOrThrowAsync(CancellationToken.None);
            await timeoutDispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RetryMiddlewareAndCircuitBreaker_RecoverFromPeerFailures()
    {
        var backendPort = TestPortAllocator.GetRandomPort();
        var backendAddress = new Uri($"http://127.0.0.1:{backendPort}");

        var backendOptions = new DispatcherOptions("resiliency-peers-backend");
        var backendInbound = new GrpcInbound([backendAddress.ToString()]);
        backendOptions.AddLifecycle("resiliency-peers-grpc", backendInbound);
        var backendDispatcher = new Dispatcher.Dispatcher(backendOptions);
        backendDispatcher.RegisterUnary("resiliency-backend::echo", builder =>
        {
            builder.Handle((request, _) =>
            {
                var meta = new ResponseMeta(encoding: RawCodec.DefaultEncoding);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(request.Body, meta)));
            });
        });

        var httpPort = TestPortAllocator.GetRandomPort();
        var httpBase = new Uri($"http://127.0.0.1:{httpPort}/");
        var unreachablePort = TestPortAllocator.GetRandomPort();
        var unreachableAddress = new Uri($"http://127.0.0.1:{unreachablePort}");

        var clientOptions = new DispatcherOptions("resiliency-peers-client");
        var httpInbound = new HttpInbound([httpBase.ToString()]);
        clientOptions.AddLifecycle("resiliency-peers-http", httpInbound);

        var grpcOutbound = new GrpcOutbound(
            [unreachableAddress, backendAddress],
            remoteService: "resiliency-peers-backend",
            peerCircuitBreakerOptions: new PeerCircuitBreakerOptions
            {
                BaseDelay = TimeSpan.FromMilliseconds(50),
                MaxDelay = TimeSpan.FromMilliseconds(200),
                FailureThreshold = 1
            });
        clientOptions.AddLifecycle("resiliency-peers-outbound", grpcOutbound);
        clientOptions.AddUnaryOutbound("resiliency-peers-backend", null, grpcOutbound);
        clientOptions.UnaryOutboundMiddleware.Add(new RetryMiddleware(new RetryOptions
        {
            Policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.FromMilliseconds(25)))
        }));

        var proxyDispatcher = new Dispatcher.Dispatcher(clientOptions);
        var rawCodec = new RawCodec();
        UnaryClient<byte[], byte[]>? backendClient = null;

        proxyDispatcher.Register(new UnaryProcedureSpec(
            "resiliency-peers-client",
            "resiliency::proxy",
            async (request, token) =>
            {
                backendClient ??= proxyDispatcher.CreateUnaryClient("resiliency-peers-backend", rawCodec);
                var outboundRequest = new Request<byte[]>(
                    new RequestMeta("resiliency-peers-backend", "resiliency-backend::echo", encoding: rawCodec.Encoding, transport: GrpcTransport),
                    request.Body.ToArray());

                var outboundResult = await backendClient.CallAsync(outboundRequest, token).ConfigureAwait(false);
                if (outboundResult.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(outboundResult.Error!);
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(outboundResult.Value.Body.AsMemory(), outboundResult.Value.Meta);
                return Ok(response);
            }));

        var ct = TestContext.Current.CancellationToken;
        await backendDispatcher.StartOrThrowAsync(ct);
        await proxyDispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(backendAddress, ct);
        await WaitForHttpReadyAsync(httpBase, ct);

        try
        {
            using var httpClient = new HttpClient { BaseAddress = httpBase };
            using var request = CreateHttpRequest("resiliency::proxy", payload: "hello"u8.ToArray());
            using var response = await httpClient.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct);
            Assert.Equal("hello", Encoding.UTF8.GetString(bodyBytes));

            var snapshot = Assert.IsType<GrpcOutboundSnapshot>(grpcOutbound.GetOutboundDiagnostics());
            Assert.Equal(2, snapshot.PeerSummaries.Count);

            var failingPeer = snapshot.PeerSummaries.Single(summary => summary.Address == unreachableAddress);
            Assert.True(failingPeer.FailureCount >= 1);
            Assert.Equal(PeerState.Unavailable, failingPeer.State);

            var healthyPeer = snapshot.PeerSummaries.Single(summary => summary.Address == backendAddress);
            Assert.True(healthyPeer.SuccessCount >= 1);
            Assert.Equal(PeerState.Available, healthyPeer.State);
        }
        finally
        {
            await proxyDispatcher.StopOrThrowAsync(CancellationToken.None);
            await backendDispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    private static readonly Marshaller<byte[]> ByteMarshaller = Marshallers.Create(
        payload => payload ?? [],
        payload => payload ?? []);

    private const string GrpcTransport = "grpc";
    private const string TransportMetadataKey = "omnirelay.transport";
    private const string RetryableMetadataKey = "omnirelay.retryable";

    private static HttpRequestMessage CreateHttpRequest(string procedure, byte[]? payload = null, string encoding = RawCodec.DefaultEncoding, int? ttlMs = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.Add(HttpTransportHeaders.Procedure, procedure);
        request.Headers.Add(HttpTransportHeaders.Transport, "http");
        request.Headers.Add(HttpTransportHeaders.Encoding, encoding);
        if (ttlMs is { } ttl)
        {
            request.Headers.Add(HttpTransportHeaders.TtlMs, ttl.ToString(CultureInfo.InvariantCulture));
        }

        var body = payload ?? Encoding.UTF8.GetBytes(procedure);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Octet);

        return request;
    }

    private static void AssertRetryAfter(HttpResponseHeaders headers)
    {
        Assert.True(headers.TryGetValues("Retry-After", out var values));
        Assert.Contains("1", values);
    }

    private static void AssertHeader(HttpResponseHeaders headers, string name, string expected)
    {
        Assert.True(headers.TryGetValues(name, out var values));
        Assert.Contains(expected, values);
    }

    private static async Task WaitForHttpReadyAsync(Uri baseAddress, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync("/healthz", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException($"HTTP inbound at {baseAddress} failed to respond to /healthz.");
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port).WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                await Task.Delay(50, cancellationToken);
                return;
            }
            catch (SocketException)
            {
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException($"The gRPC inbound at {address} failed to bind in time.");
    }
}
