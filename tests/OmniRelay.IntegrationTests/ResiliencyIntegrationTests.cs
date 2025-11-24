using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Hugo.Policies;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using Xunit;
using static AwesomeAssertions.FluentActions;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class ResiliencyIntegrationTests
{
    [Fact(Timeout = 60_000)]
    public async ValueTask HttpInbound_StopAsync_DrainsAndRejectsNewRequests()
    {
        var httpPort = TestPortAllocator.GetRandomPort();
        var httpBase = new Uri($"http://127.0.0.1:{httpPort}/");

        var options = new DispatcherOptions("resiliency-http-drain");
        var httpInbound = HttpInbound.TryCreate([httpBase]).ValueOrChecked();
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
        await dispatcher.StartAsyncChecked(ct);
        await WaitForHttpReadyAsync(httpBase, ct);

        using var httpClient = new HttpClient { BaseAddress = httpBase };
        using var initialHttpRequest = CreateHttpRequest("resiliency::slow");
        var inflight = httpClient.SendAsync(initialHttpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await startedSignal.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsyncChecked(ct);

        await Task.Delay(100, ct);

        using (var rejectedRequest = CreateHttpRequest("resiliency::slow"))
        using (var rejectedResponse = await httpClient.SendAsync(rejectedRequest, ct))
        {
            rejectedResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            AssertRetryAfter(rejectedResponse.Headers);
            await rejectedResponse.Content.ReadAsByteArrayAsync(ct);
        }

        stopTask.IsCompleted.Should().BeFalse();

        releaseSignal.TrySetResult();

        using (var response = await inflight)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await response.Content.ReadAsByteArrayAsync(ct);
        }

        await stopTask;
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask GrpcInbound_StopAsync_DrainsAndRejectsNewCalls()
    {
        var grpcPort = TestPortAllocator.GetRandomPort();
        var grpcAddress = new Uri($"http://127.0.0.1:{grpcPort}");

        var options = new DispatcherOptions("resiliency-grpc-drain");
        var grpcInbound = GrpcInbound.TryCreate([grpcAddress]).ValueOrChecked();
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
        await dispatcher.StartAsyncChecked(ct);
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

        var stopTask = dispatcher.StopAsyncChecked(ct);
        await Task.Delay(100, ct);

        var rejectedCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
        var rejection = await Invoking(() => rejectedCall.ResponseAsync).Should().ThrowAsync<RpcException>();
        rejectedCall.Dispose();
        rejection.Which.StatusCode.Should().Be(StatusCode.Unavailable);
        rejection.Which.Trailers.GetValue("retry-after").Should().Be("1");

        stopTask.IsCompleted.Should().BeFalse();

        releaseSignal.TrySetResult();

        var inflightResponse = await inflight.ResponseAsync.WaitAsync(ct);
        inflightResponse.Should().BeEmpty();

        await stopTask;
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask GrpcOutbound_WithMutualTlsRequirement_SurfacesRetryableMetadata()
    {
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-resiliency-handshake");

        var backendPort = TestPortAllocator.GetRandomPort();
        var backendAddress = new Uri($"https://127.0.0.1:{backendPort}");
        var backendOptions = new DispatcherOptions("resiliency-handshake-backend");
        var backendInbound = GrpcInbound.TryCreate([backendAddress], serverTlsOptions: new GrpcServerTlsOptions
        {
            Certificate = certificate,
            ClientCertificateMode = ClientCertificateMode.RequireCertificate,
            ClientCertificateValidation = static (_, _, _) => true
        }).ValueOrChecked();
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
        await backendDispatcher.StartAsyncChecked(ct);
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
            result.IsFailure.Should().BeTrue();

            var error = result.Error!;
            error.Code.Should().Be("unavailable");
            error.TryGetMetadata(TransportMetadataKey, out string? transport).Should().BeTrue();
            transport.Should().Be(GrpcTransport);
            error.TryGetMetadata(RetryableMetadataKey, out bool retryable).Should().BeTrue();
            retryable.Should().BeTrue();
        }
        finally
        {
            await outbound.StopAsync(CancellationToken.None);
            await backendDispatcher.StopAsyncChecked(CancellationToken.None);
        }
    }

    [Http3Fact(Timeout = 90_000)]
    public async ValueTask GrpcClient_WithHttp3Preferred_FallsBackToHttp2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-h3-fallback");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var backendOptions = new DispatcherOptions("resiliency-h3-backend");
        var inbound = GrpcInbound.TryCreate(
            [address],
            serverTlsOptions: new GrpcServerTlsOptions { Certificate = certificate },
            serverRuntimeOptions: new GrpcServerRuntimeOptions { EnableHttp3 = false })
            .ValueOrChecked();
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
        await backendDispatcher.StartAsyncChecked(ct);
        await outbound.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var request = new Request<byte[]>(
                new RequestMeta("resiliency-h3-backend", "resiliency::h3probe", encoding: codec.Encoding, transport: GrpcTransport),
                []);

            var result = await client.CallAsync(request, ct);
            result.IsSuccess.Should().BeTrue();

            var observed = await protocolCapture.Task.WaitAsync(ct);
            observed.Should().Be("HTTP/2");
        }
        finally
        {
            await outbound.StopAsync(CancellationToken.None);
            await backendDispatcher.StopAsyncChecked(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask DeadlinesAndCancellations_SurfaceStatusesAndRetryHints()
    {
        var timeoutPort = TestPortAllocator.GetRandomPort();
        var timeoutBase = new Uri($"http://127.0.0.1:{timeoutPort}/");

        var timeoutOptions = new DispatcherOptions("resiliency-deadlines");
        var timeoutInbound = HttpInbound.TryCreate([timeoutBase]).ValueOrChecked();
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
        var cancelInbound = GrpcInbound.TryCreate([cancelAddress]).ValueOrChecked();
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
        await timeoutDispatcher.StartAsyncChecked(ct);
        await cancelDispatcher.StartAsyncChecked(ct);
        await WaitForHttpReadyAsync(timeoutBase, ct);
        await WaitForGrpcReadyAsync(cancelAddress, ct);

        try
        {
            using (var httpClient = new HttpClient { BaseAddress = timeoutBase })
            using (var request = CreateHttpRequest("resiliency::timeout", ttlMs: 50))
            using (var response = await httpClient.SendAsync(request, ct))
            {
                response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
                AssertHeader(response.Headers, HttpTransportHeaders.Status, OmniRelayStatusCode.DeadlineExceeded.ToString());

                var json = await response.Content.ReadAsStringAsync(ct);
                using var document = JsonDocument.Parse(json);
                var metadata = document.RootElement.GetProperty("metadata");
                var retryElement = metadata.GetProperty(RetryableMetadataKey);
                var retryable = retryElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(retryElement.GetString(), out var parsed) && parsed,
                    _ => false
                };
                retryable.Should().BeTrue();
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
            var rpcException = await Invoking(() => call.ResponseAsync).Should().ThrowAsync<RpcException>();
            rpcException.Which.StatusCode.Should().Be(StatusCode.Cancelled);
        }
        finally
        {
            await cancelDispatcher.StopAsyncChecked(CancellationToken.None);
            await timeoutDispatcher.StopAsyncChecked(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask RetryMiddlewareAndCircuitBreaker_RecoverFromPeerFailures()
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
        var httpInbound = HttpInbound.TryCreate([httpBase]).ValueOrChecked();
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
                if (backendClient is null)
                {
                    var createResult = proxyDispatcher.CreateUnaryClient("resiliency-peers-backend", rawCodec);
                    if (createResult.IsFailure)
                    {
                        return Err<Response<ReadOnlyMemory<byte>>>(createResult.Error!);
                    }

                    backendClient = createResult.Value;
                }
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
        await backendDispatcher.StartAsyncChecked(ct);
        await proxyDispatcher.StartAsyncChecked(ct);
        await WaitForGrpcReadyAsync(backendAddress, ct);
        await WaitForHttpReadyAsync(httpBase, ct);

        try
        {
            using var httpClient = new HttpClient { BaseAddress = httpBase };
            using var request = CreateHttpRequest("resiliency::proxy", payload: "hello"u8.ToArray());
            using var response = await httpClient.SendAsync(request, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct);
            Encoding.UTF8.GetString(bodyBytes).Should().Be("hello");

            var snapshot = grpcOutbound.GetOutboundDiagnostics().Should().BeOfType<GrpcOutboundSnapshot>().Subject;
            snapshot.PeerSummaries.Count.Should().Be(2);

            var failingPeer = snapshot.PeerSummaries.Single(summary => summary.Address == unreachableAddress);
            failingPeer.FailureCount.Should().BeGreaterThanOrEqualTo(1);
            failingPeer.State.Should().Be(PeerState.Unavailable);

            var healthyPeer = snapshot.PeerSummaries.Single(summary => summary.Address == backendAddress);
            healthyPeer.SuccessCount.Should().BeGreaterThanOrEqualTo(1);
            healthyPeer.State.Should().Be(PeerState.Available);
        }
        finally
        {
            await proxyDispatcher.StopAsyncChecked(CancellationToken.None);
            await backendDispatcher.StopAsyncChecked(CancellationToken.None);
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
        headers.TryGetValues("Retry-After", out var values).Should().BeTrue();
        values.Should().Contain("1");
    }

    private static void AssertHeader(HttpResponseHeaders headers, string name, string expected)
    {
        headers.TryGetValues(name, out var values).Should().BeTrue();
        values.Should().Contain(expected);
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
