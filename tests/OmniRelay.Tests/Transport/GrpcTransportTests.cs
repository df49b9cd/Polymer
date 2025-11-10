using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Health.V1;
using Grpc.Net.Client;
using Grpc.Net.Compression;
using Hugo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Tests.Support;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Transport;

public class GrpcTransportTests
{
    static GrpcTransportTests()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    private const string TransportName = "grpc";
    private const string EncodingHeaderKey = "rpc-encoding";
    private const string StatusTrailerKey = "omnirelay-status";
    private const string EncodingTrailerKey = "omnirelay-encoding";
    private const string ErrorMessageTrailerKey = "omnirelay-error-message";
    private const string ErrorCodeTrailerKey = "omnirelay-error-code";

    [Fact]
    public void CompressionOptions_ValidateRequiresRegisteredAlgorithm()
    {
        var options = new GrpcCompressionOptions
        {
            Providers = [],
            DefaultAlgorithm = "gzip"
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void GrpcOutbound_CreateCallOptionsAddsAcceptEncoding()
    {
        var provider = new DummyCompressionProvider("gzip");
        var compressionOptions = new GrpcCompressionOptions
        {
            Providers = [provider],
            DefaultAlgorithm = provider.EncodingName
        };

        var outbound = new GrpcOutbound(new Uri("http://127.0.0.1:5000"), "echo", compressionOptions: compressionOptions);
        var requestMeta = new RequestMeta(service: "echo", procedure: "echo::call", transport: GrpcTransportConstants.TransportName);

        var createCallOptions = typeof(GrpcOutbound).GetMethod("CreateCallOptions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate CreateCallOptions method.");

        var callOptions = (CallOptions)createCallOptions.Invoke(outbound, [requestMeta, CancellationToken.None])!;
        var headers = callOptions.Headers ?? [];
        var acceptEncoding = headers.GetValue(GrpcTransportConstants.GrpcAcceptEncodingHeader);

        Assert.Equal(provider.EncodingName, acceptEncoding);
    }

    [Fact]
    public void GrpcOutbound_CreateCallOptionsPreservesExistingAcceptEncoding()
    {
        var provider = new DummyCompressionProvider("gzip");
        var compressionOptions = new GrpcCompressionOptions
        {
            Providers = [provider],
            DefaultAlgorithm = provider.EncodingName
        };

        var outbound = new GrpcOutbound(new Uri("http://127.0.0.1:5001"), "echo", compressionOptions: compressionOptions);
        var requestHeaders = new[]
        {
            new KeyValuePair<string, string>(GrpcTransportConstants.GrpcAcceptEncodingHeader, "identity")
        };
        var requestMeta = new RequestMeta(service: "echo", procedure: "echo::call", transport: GrpcTransportConstants.TransportName, headers: requestHeaders);

        var createCallOptions = typeof(GrpcOutbound).GetMethod("CreateCallOptions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate CreateCallOptions method.");

        var callOptions = (CallOptions)createCallOptions.Invoke(outbound, [requestMeta, CancellationToken.None])!;
        var callHeaders = callOptions.Headers ?? [];
        var acceptEncoding = callHeaders.GetValue(GrpcTransportConstants.GrpcAcceptEncodingHeader);

        Assert.Equal("identity", acceptEncoding);
    }

    [Fact]
    public async Task GrpcOutbound_CallBeforeStart_Throws()
    {
        var outbound = new GrpcOutbound(new Uri("http://127.0.0.1:5000"), "echo");
        var requestMeta = new RequestMeta(service: "echo", procedure: "echo::ping", transport: TransportName);
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await outbound.CallAsync(request, CancellationToken.None));
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerStreaming_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new StreamProcedureSpec(
            "stream",
            "stream::events",
            (request, callOptions, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<IStreamCall>(decode.Error!));
                }

                var streamCall = GrpcServerStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                var backgroundTask = Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            var response = new EchoResponse { Message = $"event-{i}" };
                            var encode = codec.EncodeResponse(response, streamCall.ResponseMeta);
                            if (encode.IsFailure)
                            {
                                await streamCall.CompleteAsync(encode.Error!);
                                return;
                            }

                            await streamCall.WriteAsync(encode.Value, cancellationToken);
                            await Task.Delay(20, cancellationToken);
                        }
                    }
                    finally
                    {
                        await streamCall.CompleteAsync();
                    }
                }, cancellationToken);
                serverTasks.Track(backgroundTask);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateStreamClient("stream", codec);
            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::events",
                encoding: "application/json",
                transport: "grpc");
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("seed"));

            var responses = new List<string>();
            await foreach (var response in client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct))
            {
                responses.Add(response.ValueOrThrow().Body.Message);
            }

            Assert.Equal(new[] { "event-0", "event-1", "event-2" }, responses);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcInbound_BindsConfiguredHttp2EndpointsOnly()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("binding");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            "binding",
            "binding::ping",
            (request, cancellationToken) =>
                ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var outbound = new GrpcOutbound(address, "binding");
        await outbound.StartAsync(ct);

        try
        {
            var requestMeta = new RequestMeta(
                service: "binding",
                procedure: "binding::ping",
                transport: TransportName);
            var request = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);
            var response = await outbound.CallAsync(request, ct);
            Assert.True(response.IsSuccess, response.Error?.Message);

            var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var unusedPort = TestPortAllocator.GetRandomPort();
                using var unusedClient = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(200));
                await unusedClient.ConnectAsync("127.0.0.1", unusedPort, timeoutCts.Token);
            });

            Assert.True(
                exception is SocketException or OperationCanceledException,
                $"Expected connection failure but observed {exception.GetType().FullName}.");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task StopAsync_WaitsForActiveGrpcCallsAndRejectsNewOnes()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("lifecycle");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

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
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var channel = GrpcChannel.ForAddress(address);
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "lifecycle", "test::slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        var inFlightCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopOrThrowAsync(ct);

        await Task.Delay(100, ct);

        var rejectedCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
        var rejection = await Assert.ThrowsAsync<RpcException>(() => rejectedCall.ResponseAsync);
        Assert.Equal(StatusCode.Unavailable, rejection.StatusCode);
        Assert.Equal("1", rejection.Trailers.GetValue("retry-after"));
        Assert.False(stopTask.IsCompleted);

        releaseRequest.TrySetResult();
        var response = await inFlightCall.ResponseAsync.WaitAsync(ct);
        Assert.Empty(response);

        await stopTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task StopAsyncCancellation_CompletesWithoutDrainingGrpcCalls()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("lifecycle");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

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
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var channel = GrpcChannel.ForAddress(address);
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "lifecycle", "test::slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        var inFlightCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
        await requestStarted.Task.WaitAsync(ct);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopTask = dispatcher.StopOrThrowAsync(stopCts.Token);

        await stopTask;
        Assert.False(releaseRequest.Task.IsCompleted);
        releaseRequest.TrySetResult();

        try
        {
            await inFlightCall.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (RpcException)
        {
        }
        catch (Exception) when (true)
        {
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcInbound_WithTlsCertificate_BindsAndServes()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-test");

        var options = new DispatcherOptions("tls");
        var tlsOptions = new GrpcServerTlsOptions { Certificate = certificate };
        var grpcInbound = new GrpcInbound([address.ToString()], serverTlsOptions: tlsOptions);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            "tls",
            "tls::ping",
            (request, cancellationToken) =>
                ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var clientTls = new GrpcClientTlsOptions
        {
            ServerCertificateValidationCallback = static (_, _, _, _) => true
        };
        var outbound = new GrpcOutbound(address, "tls", clientTlsOptions: clientTls);
        await outbound.StartAsync(ct);

        try
        {
            var requestMeta = new RequestMeta(
                service: "tls",
                procedure: "tls::ping",
                transport: TransportName);
            var request = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);
            var response = await outbound.CallAsync(request, ct);
            Assert.True(response.IsSuccess, response.Error?.Message);
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcHealthService_ReflectsReadiness()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("health");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "health",
            "slow",
            async (request, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var channel = GrpcChannel.ForAddress(address);
        var healthClient = new Health.HealthClient(channel);

        var healthy = await healthClient.CheckAsync(new HealthCheckRequest(), cancellationToken: ct).ResponseAsync.WaitAsync(ct);
        Assert.Equal(HealthCheckResponse.Types.ServingStatus.Serving, healthy.Status);

        var method = new Method<byte[], byte[]>(MethodType.Unary, "health", "slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var invoker = channel.CreateCallInvoker();
        var inFlightCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopOrThrowAsync(ct);

        var draining = await healthClient.CheckAsync(new HealthCheckRequest(), cancellationToken: ct).ResponseAsync.WaitAsync(ct);
        Assert.Equal(HealthCheckResponse.Types.ServingStatus.NotServing, draining.Status);

        releaseRequest.TrySetResult();
        await inFlightCall.ResponseAsync.WaitAsync(ct);
        await stopTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerStreaming_ErrorMidStream_PropagatesToClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new StreamProcedureSpec(
            "stream",
            "stream::fails",
            (request, callOptions, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<IStreamCall>(decode.Error!));
                }

                var streamCall = GrpcServerStreamCall.Create(
                    request.Meta,
                    new ResponseMeta(encoding: "application/json"));

                var background = Task.Run(async () =>
                {
                    try
                    {
                        var first = codec.EncodeResponse(new EchoResponse { Message = "first" }, streamCall.ResponseMeta);
                        if (first.IsFailure)
                        {
                            await streamCall.CompleteAsync(first.Error!, cancellationToken);
                            return;
                        }

                        await streamCall.WriteAsync(first.Value, cancellationToken);

                        var error = OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Internal,
                            "stream failure",
                            transport: TransportName);
                        await streamCall.CompleteAsync(error, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        await streamCall.CompleteAsync(OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Internal,
                            ex.Message ?? "unexpected failure",
                            transport: TransportName), cancellationToken);
                    }
                }, cancellationToken);

                serverTasks.Track(background);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateStreamClient("stream", codec);
            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::fails",
                encoding: "application/json",
                transport: "grpc");
            var payload = new string('x', 4096);
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest(payload));

            var enumerator = client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct)
                .GetAsyncEnumerator(ct);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("first", enumerator.Current.ValueOrThrow().Body.Message);

            var exception = await Assert.ThrowsAsync<OmniRelayException>(async () =>
            {
                await enumerator.MoveNextAsync();
            });

            await enumerator.DisposeAsync();

            Assert.Equal(OmniRelayStatusCode.Internal, exception.StatusCode);
            Assert.Contains("stream failure", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcOutbound_RoundRobinPeers_RecordSuccessAcrossEndpoints()
    {
        var port1 = TestPortAllocator.GetRandomPort();
        var port2 = TestPortAllocator.GetRandomPort();
        var address1 = new Uri($"http://127.0.0.1:{port1}");
        var address2 = new Uri($"http://127.0.0.1:{port2}");

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound([address1.ToString(), address2.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "echo::ping",
            (request, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decode.Error!));
                }

                var payload = new EchoResponse { Message = decode.Value.Message };
                var responseMeta = new ResponseMeta(encoding: "application/json");
                var encode = codec.EncodeResponse(payload, responseMeta);
                return encode.IsSuccess
                    ? ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta)))
                    : ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address1, ct);
        await WaitForGrpcReadyAsync(address2, ct);

        var outbound = new GrpcOutbound([address1, address2], "echo");
        await outbound.StartAsync(ct);

        try
        {
            for (var i = 0; i < 4; i++)
            {
                var meta = new RequestMeta(
                    service: "echo",
                    procedure: "echo::ping",
                    encoding: "application/json",
                    transport: "grpc");
                var requestPayload = codec.EncodeRequest(new EchoRequest($"call-{i}"), meta);
                Assert.True(requestPayload.IsSuccess);
                var request = new Request<ReadOnlyMemory<byte>>(meta, requestPayload.Value);

                var responseResult = await outbound.CallAsync(request, ct);
                Assert.True(responseResult.IsSuccess, responseResult.Error?.Message);

                var decode = codec.DecodeResponse(responseResult.Value.Body, responseResult.Value.Meta);
                Assert.True(decode.IsSuccess, decode.Error?.Message);
                Assert.Equal($"call-{i}", decode.Value.Message);
            }

            var snapshot = Assert.IsType<GrpcOutboundSnapshot>(outbound.GetOutboundDiagnostics());
            Assert.Equal(2, snapshot.PeerSummaries.Count);
            Assert.All(snapshot.PeerSummaries, peer =>
            {
                Assert.True(peer.LastSuccess.HasValue);
                Assert.True(peer.SuccessCount > 0);
                Assert.NotNull(peer.AverageLatencyMs);
                Assert.NotNull(peer.P50LatencyMs);
            });
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcOutbound_FailingPeerTriggersCircuitBreaker()
    {
        var healthyPort = TestPortAllocator.GetRandomPort();
        var failingPort = TestPortAllocator.GetRandomPort();
        var healthyAddress = new Uri($"http://127.0.0.1:{healthyPort}");
        var failingAddress = new Uri($"http://127.0.0.1:{failingPort}");

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound([healthyAddress.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "echo::ping",
            (request, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decode.Error!));
                }

                var payload = new EchoResponse { Message = decode.Value.Message };
                var responseMeta = new ResponseMeta(encoding: "application/json");
                var encode = codec.EncodeResponse(payload, responseMeta);
                return encode.IsSuccess
                    ? ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta)))
                    : ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(healthyAddress, ct);

        var breakerOptions = new PeerCircuitBreakerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromMilliseconds(200),
            FailureThreshold = 1
        };

        var outbound = new GrpcOutbound(
            [failingAddress, healthyAddress],
            "echo",
            peerCircuitBreakerOptions: breakerOptions,
            peerChooser: peers => new RoundRobinPeerChooser(ImmutableArray.CreateRange(peers)));

        await outbound.StartAsync(ct);

        try
        {
            var failingMeta = new RequestMeta(
                service: "echo",
                procedure: "echo::ping",
                encoding: "application/json",
                transport: "grpc");
            var failingPayload = codec.EncodeRequest(new EchoRequest("first"), failingMeta);
            Assert.True(failingPayload.IsSuccess);
            var failingRequest = new Request<ReadOnlyMemory<byte>>(failingMeta, failingPayload.Value);

            var failureResult = await outbound.CallAsync(failingRequest, ct);
            Assert.True(failureResult.IsFailure);

            var successMeta = new RequestMeta(
                service: "echo",
                procedure: "echo::ping",
                encoding: "application/json",
                transport: "grpc");
            var successPayload = codec.EncodeRequest(new EchoRequest("second"), successMeta);
            Assert.True(successPayload.IsSuccess);
            var successRequest = new Request<ReadOnlyMemory<byte>>(successMeta, successPayload.Value);

            var attempts = 0;
            Result<Response<ReadOnlyMemory<byte>>> successResult;
            do
            {
                successResult = await outbound.CallAsync(successRequest, ct);
                if (successResult.IsSuccess)
                {
                    break;
                }

                attempts++;
                await Task.Delay(200, ct);
            }
            while (attempts < 3);

            Assert.True(successResult.IsSuccess, successResult.Error?.Message ?? "Unable to reach healthy gRPC peer after retries");

            var decode = codec.DecodeResponse(successResult.Value.Body, successResult.Value.Meta);
            Assert.True(decode.IsSuccess, decode.Error?.Message);
            Assert.Equal("second", decode.Value.Message);

            var snapshot = Assert.IsType<GrpcOutboundSnapshot>(outbound.GetOutboundDiagnostics());
            Assert.Equal(2, snapshot.PeerSummaries.Count);
            var failingPeerSummary = Assert.Single(snapshot.PeerSummaries, peer => peer.Address == failingAddress);
            Assert.True(failingPeerSummary.LastFailure.HasValue);
            Assert.True(failingPeerSummary.FailureCount > 0);
            var healthyPeerSummary = Assert.Single(snapshot.PeerSummaries, peer => peer.Address == healthyAddress);
            Assert.True(healthyPeerSummary.LastSuccess.HasValue);
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerStreaming_PayloadAboveLimit_FaultsStream()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream-limit");
        var runtime = new GrpcServerRuntimeOptions { ServerStreamMaxMessageBytes = 8 };
        var grpcInbound = new GrpcInbound([address.ToString()], serverRuntimeOptions: runtime);
        options.AddLifecycle("grpc-inbound", grpcInbound);
        var grpcOutbound = new GrpcOutbound(address, "stream-limit");
        options.AddStreamOutbound("stream-limit", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new StreamProcedureSpec(
            "stream-limit",
            "stream::oversized",
            (request, callOptions, cancellationToken) =>
            {
                _ = callOptions;
                var streamCall = GrpcServerStreamCall.Create(request.Meta, new ResponseMeta());

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = Enumerable.Repeat((byte)0x42, 32).ToArray();
                        await streamCall.WriteAsync(payload, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore cancellation from aborted response
                    }
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var rawCodec = new RawCodec();
        var client = dispatcher.CreateStreamClient<byte[], byte[]>("stream-limit", rawCodec);
        var requestMeta = new RequestMeta(
            service: "stream-limit",
            procedure: "stream::oversized",
            encoding: RawCodec.DefaultEncoding,
            transport: TransportName);
        var request = new Request<byte[]>(requestMeta, []);

        var exception = await Assert.ThrowsAsync<OmniRelayException>(async () =>
        {
            await using var enumerator = client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct).GetAsyncEnumerator(ct);
            await enumerator.MoveNextAsync();
        });

        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, exception.StatusCode);

        await dispatcher.StopOrThrowAsync(ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");

        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::aggregate",
            async (context, cancellationToken) =>
            {
                var totalBytes = 0;
                var reader = context.Requests;

                while (await reader.WaitToReadAsync(cancellationToken))
                {
                    while (reader.TryRead(out var payload))
                    {
                        var decodeResult = codec.DecodeRequest(payload, context.Meta);
                        if (decodeResult.IsFailure)
                        {
                            return Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!);
                        }

                        totalBytes += decodeResult.Value.Amount;
                    }
                }

                var aggregateResponse = new AggregateResponse(totalBytes);
                var responseMeta = new ResponseMeta(encoding: codec.Encoding);
                var encodeResponse = codec.EncodeResponse(aggregateResponse, responseMeta);
                if (encodeResponse.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(encodeResponse.Error!);
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encodeResponse.Value, responseMeta);
                return Ok(response);
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient("stream", codec);

            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::aggregate",
                encoding: codec.Encoding,
                transport: "grpc");

            var streamResult = await client.StartAsync(requestMeta, ct);
            await using var stream = streamResult.ValueOrThrow();

            var firstWrite = await stream.WriteAsync(new AggregateChunk(Amount: 2), ct);
            firstWrite.ThrowIfFailure();
            var secondWrite = await stream.WriteAsync(new AggregateChunk(Amount: 5), ct);
            secondWrite.ThrowIfFailure();
            await stream.CompleteAsync(ct);

            var responseResult = await stream.Response;
            var response = responseResult.ValueOrThrow();

            Assert.Equal(7, response.Body.TotalAmount);
            Assert.Equal(codec.Encoding, stream.ResponseMeta.Encoding);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_CancellationFromClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");
        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::aggregate",
            async (context, cancellationToken) =>
            {
                await foreach (var _ in context.Requests.ReadAllAsync(cancellationToken))
                {
                    // Simply drain until cancellation.
                }

                return Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.Cancelled,
                    "cancelled"));
            }));

        var cts = new CancellationTokenSource();
        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient("stream", codec);
            var requestMeta = new RequestMeta(service: "stream", procedure: "stream::aggregate", encoding: codec.Encoding, transport: "grpc");

            var streamResult = await client.StartAsync(requestMeta, ct);
            await using var stream = streamResult.ValueOrThrow();

            await cts.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await stream.WriteAsync(new AggregateChunk(Amount: 1), cts.Token);
            });
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_DeadlineExceededMapsStatus()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");

        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::deadline",
            async (context, cancellationToken) =>
            {
                Assert.True(context.Meta.Deadline.HasValue);
                await foreach (var _ in context.Requests.ReadAllAsync(cancellationToken))
                {
                }

                return Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.DeadlineExceeded,
                    "deadline exceeded"));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient("stream", codec);
            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::deadline",
                encoding: codec.Encoding,
                transport: "grpc",
                deadline: DateTimeOffset.UtcNow.AddMilliseconds(200));

            var streamResult = await client.StartAsync(requestMeta, ct);
            await using var stream = streamResult.ValueOrThrow();
            await stream.CompleteAsync(ct);

            var responseResult = await stream.Response;
            Assert.True(responseResult.IsFailure);
            Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(responseResult.Error!));
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_LargePayloadChunks()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");

        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::huge",
            async (context, cancellationToken) =>
            {
                var total = 0;
                await foreach (var payload in context.Requests.ReadAllAsync(cancellationToken))
                {
                    var decode = codec.DecodeRequest(payload, context.Meta);
                    if (decode.IsFailure)
                    {
                        return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
                    }

                    total += decode.Value.Amount;
                }

                var response = new AggregateResponse(total);
                var responseMeta = new ResponseMeta(encoding: codec.Encoding);
                var encode = codec.EncodeResponse(response, responseMeta);
                if (encode.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(encode.Error!);
                }

                return Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient("stream", codec);
            var requestMeta = new RequestMeta(service: "stream", procedure: "stream::huge", encoding: codec.Encoding, transport: "grpc");

            var streamResult = await client.StartAsync(requestMeta, ct);
            await using var stream = streamResult.ValueOrThrow();

            const int chunkCount = 1_000;
            for (var i = 0; i < chunkCount; i++)
            {
                var writeResult = await stream.WriteAsync(new AggregateChunk(1), ct);
                writeResult.ThrowIfFailure();
            }

            await stream.CompleteAsync(ct);

            var responseResult = await stream.Response;
            var response = responseResult.ValueOrThrow();
            Assert.Equal(chunkCount, response.Body.TotalAmount);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_ServerErrorPropagatesToClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");

        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::server-error",
            async (context, cancellationToken) =>
            {
                var chunks = 0;
                await foreach (var payload in context.Requests.ReadAllAsync(cancellationToken))
                {
                    var decode = codec.DecodeRequest(payload, context.Meta);
                    if (decode.IsFailure)
                    {
                        return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
                    }

                    chunks += decode.Value.Amount;
                    if (chunks >= 1)
                    {
                        return Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Unavailable,
                            "service unavailable",
                            transport: TransportName));
                    }
                }

                return Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.Internal,
                    "no data received",
                    transport: TransportName));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient("stream", codec);
            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::server-error",
                encoding: codec.Encoding,
                transport: "grpc");

            var streamResult = await client.StartAsync(requestMeta, ct);
            await using var stream = streamResult.ValueOrThrow();

            var writeResult = await stream.WriteAsync(new AggregateChunk(Amount: 1), ct);
            writeResult.ThrowIfFailure();
            await stream.CompleteAsync(ct);

            var responseResult = await stream.Response;
            Assert.True(responseResult.IsFailure);
            Assert.Equal(OmniRelayStatusCode.Unavailable, OmniRelayErrorAdapter.ToStatus(responseResult.Error!));
            Assert.Contains("service unavailable", responseResult.Error!.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Unary_ClientInterceptorExecutes()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var clientInterceptor = new RecordingClientInterceptor();

        var clientRuntimeOptions = new GrpcClientRuntimeOptions
        {
            Interceptors = [clientInterceptor]
        };

        var dispatcherOptions = new DispatcherOptions("intercept");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        dispatcherOptions.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "intercept", clientRuntimeOptions: clientRuntimeOptions);
        dispatcherOptions.AddUnaryOutbound("intercept", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(dispatcherOptions);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var observedClientHeader = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "intercept",
            "intercept::echo",
            (request, cancellationToken) =>
            {
                if (request.Meta.Headers.TryGetValue("x-client-interceptor", out var headerValue))
                {
                    observedClientHeader.TrySetResult(headerValue);
                }
                else
                {
                    observedClientHeader.TrySetResult(string.Empty);
                }

                var responsePayload = new EchoResponse { Message = request.Body.Length.ToString(CultureInfo.InvariantCulture) };
                var responseMeta = new ResponseMeta(encoding: "application/json");
                var encode = codec.EncodeResponse(responsePayload, responseMeta);
                if (encode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta);
                return ValueTask.FromResult(Ok(response));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateUnaryClient("intercept", codec);
            var requestMeta = new RequestMeta(
                service: "intercept",
                procedure: "intercept::echo",
                encoding: "application/json",
                transport: "grpc");

            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("payload"));
            var response = await client.CallAsync(request, ct);

            Assert.True(response.IsSuccess, response.Error?.Message);
            Assert.Equal("true", (await observedClientHeader.Task).ToLowerInvariant());
            Assert.Equal(1, clientInterceptor.UnaryCallCount);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Unary_ServerInterceptorExecutes()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var serverInterceptor = new RecordingServerInterceptor();
        var serverRuntimeOptions = new GrpcServerRuntimeOptions
        {
            Interceptors = [typeof(RecordingServerInterceptor)]
        };

        var dispatcherOptions = new DispatcherOptions("server-intercept");
        var grpcInbound = new GrpcInbound(
            [address.ToString()],
            services => services.AddSingleton(serverInterceptor),
            serverRuntimeOptions: serverRuntimeOptions);
        dispatcherOptions.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "server-intercept");
        dispatcherOptions.AddUnaryOutbound("server-intercept", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(dispatcherOptions);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "server-intercept",
            "server-intercept::echo",
            (request, cancellationToken) =>
            {
                var responsePayload = new EchoResponse { Message = request.Body.Length.ToString(CultureInfo.InvariantCulture) };
                var responseMeta = new ResponseMeta(encoding: "application/json");
                var encode = codec.EncodeResponse(responsePayload, responseMeta);
                if (encode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta);
                return ValueTask.FromResult(Ok(response));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateUnaryClient("server-intercept", codec);
            var requestMeta = new RequestMeta(
                service: "server-intercept",
                procedure: "server-intercept::echo",
                encoding: "application/json",
                transport: "grpc");
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("payload"));

            var response = await client.CallAsync(request, ct);

            Assert.True(response.IsSuccess, response.Error?.Message);
            Assert.Equal(1, serverInterceptor.UnaryCallCount);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task UnaryRoundtrip_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "echo");
        options.AddUnaryOutbound("echo", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "ping",
            (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!));
                }

                var responsePayload = new EchoResponse { Message = decodeResult.Value.Message + "-grpc" };
                var encodeResult = codec.EncodeResponse(responsePayload, new ResponseMeta(encoding: "application/json"));
                if (encodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!));
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, new ResponseMeta(encoding: "application/json"));
                return ValueTask.FromResult(Ok(response));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateUnaryClient("echo", codec);
            var requestMeta = new RequestMeta(
                service: "echo",
                procedure: "ping",
                encoding: "application/json",
                transport: "grpc");
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("hello"));

            var result = await client.CallAsync(request, ct);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal("hello-grpc", result.Value.Body.Message);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OnewayRoundtrip_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("audit");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "audit");
        options.AddOnewayOutbound("audit", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, object>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new OnewayProcedureSpec(
            "audit",
            "audit::record",
            (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<OnewayAck>(decodeResult.Error!));
                }

                received.TrySetResult(decodeResult.Value.Message);
                return ValueTask.FromResult(Ok(OnewayAck.Ack()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateOnewayClient("audit", codec);
            var requestMeta = new RequestMeta(
                service: "audit",
                procedure: "audit::record",
                encoding: "application/json",
                transport: "grpc");
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("ping"));

            var ackResult = await client.CallAsync(request, ct);

            Assert.True(ackResult.IsSuccess, ackResult.Error?.Message);
            Assert.Equal("ping", await received.Task.WaitAsync(TimeSpan.FromSeconds(2), ct));
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat");
        options.AddDuplexOutbound("chat", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::talk",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                var pump = Task.Run(async () =>
                {
                    try
                    {
                        var handshake = codec.EncodeResponse(new ChatMessage("ready"), call.ResponseMeta);
                        if (handshake.IsFailure)
                        {
                            await call.CompleteResponsesAsync(handshake.Error!, cancellationToken);
                            return;
                        }

                        await call.ResponseWriter.WriteAsync(handshake.Value, cancellationToken);

                        await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken))
                        {
                            var decode = codec.DecodeRequest(payload, request.Meta);
                            if (decode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(decode.Error!, cancellationToken);
                                return;
                            }

                            var response = new ChatMessage($"echo:{decode.Value.Message}");
                            var encode = codec.EncodeResponse(response, call.ResponseMeta);
                            if (encode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(encode.Error!, cancellationToken);
                                return;
                            }

                            await call.ResponseWriter.WriteAsync(encode.Value, cancellationToken);
                        }

                        await call.CompleteResponsesAsync(cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        await call.CompleteResponsesAsync(OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Internal,
                            ex.Message ?? "stream processing failure",
                            transport: TransportName), cancellationToken);
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateDuplexStreamClient("chat", codec);
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::talk",
                encoding: "application/json",
                transport: "grpc");

            var sessionResult = await client.StartAsync(requestMeta, ct);
            await using var session = sessionResult.ValueOrThrow();

            var firstWrite = await session.WriteAsync(new ChatMessage("hello"), ct);
            firstWrite.ThrowIfFailure();
            var secondWrite = await session.WriteAsync(new ChatMessage("world"), ct);
            secondWrite.ThrowIfFailure();
            await session.CompleteRequestsAsync(cancellationToken: ct);

            var responses = new List<string>();
            await foreach (var response in session.ReadResponsesAsync(ct))
            {
                responses.Add(response.ValueOrThrow().Body.Message);
            }

            Assert.Equal(new[] { "ready", "echo:hello", "echo:world" }, responses);
            Assert.Equal("application/json", session.ResponseMeta.Encoding);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_ResponseAboveLimit_FaultsStream()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat-limit");
        var runtime = new GrpcServerRuntimeOptions { DuplexMaxMessageBytes = 16 };
        var grpcInbound = new GrpcInbound([address.ToString()], serverRuntimeOptions: runtime);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat-limit");
        options.AddDuplexOutbound("chat-limit", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new DuplexProcedureSpec(
            "chat-limit",
            "chat::oversized",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta());

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = Enumerable.Repeat((byte)0x7A, 64).ToArray();
                        await call.ResponseWriter.WriteAsync(payload, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore cancellation when response is aborted
                    }
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IDuplexStreamCall>(call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var rawCodec = new RawCodec();
        var client = dispatcher.CreateDuplexStreamClient<byte[], byte[]>("chat-limit", rawCodec);
        var requestMeta = new RequestMeta(
            service: "chat-limit",
            procedure: "chat::oversized",
            encoding: RawCodec.DefaultEncoding,
            transport: TransportName);

        var callResult = await client.StartAsync(requestMeta, ct);
        await using var call = callResult.ValueOrThrow();

        await using var enumerator = call.ReadResponsesAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(enumerator.Current.IsFailure);
        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!));

        await dispatcher.StopOrThrowAsync(ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_ServerCancellationPropagatesToClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat");
        options.AddDuplexOutbound("chat", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::cancel",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                var pump = Task.Run(async () =>
                {
                    var handshake = codec.EncodeResponse(new ChatMessage("ready"), call.ResponseMeta);
                    if (handshake.IsFailure)
                    {
                        await call.CompleteResponsesAsync(handshake.Error!, cancellationToken);
                        return;
                    }

                    await call.ResponseWriter.WriteAsync(handshake.Value, cancellationToken);

                    await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken))
                    {
                        var decode = codec.DecodeRequest(payload, request.Meta);
                        if (decode.IsFailure)
                        {
                            await call.CompleteResponsesAsync(decode.Error!, cancellationToken);
                            return;
                        }

                        var response = new ChatMessage($"ack:{decode.Value.Message}");
                        var encode = codec.EncodeResponse(response, call.ResponseMeta);
                        if (encode.IsFailure)
                        {
                            await call.CompleteResponsesAsync(encode.Error!, cancellationToken);
                            return;
                        }

                        await call.ResponseWriter.WriteAsync(encode.Value, cancellationToken);

                        var error = OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Cancelled,
                            "server cancelled",
                            transport: TransportName);
                        await call.CompleteResponsesAsync(error, cancellationToken);
                        return;
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateDuplexStreamClient("chat", codec);
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::cancel",
                encoding: "application/json",
                transport: "grpc");

            var sessionResult = await client.StartAsync(requestMeta, ct);
            await using var session = sessionResult.ValueOrThrow();
            var writeResult = await session.WriteAsync(new ChatMessage("first"), ct);
            writeResult.ThrowIfFailure();
            await session.CompleteRequestsAsync(cancellationToken: ct);

            var enumerator = session.ReadResponsesAsync(ct).GetAsyncEnumerator(ct);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("ready", enumerator.Current.ValueOrThrow().Body.Message);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("ack:first", enumerator.Current.ValueOrThrow().Body.Message);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.True(enumerator.Current.IsFailure);
            Assert.Equal(OmniRelayStatusCode.Cancelled, OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!));
            Assert.Contains("server cancelled", enumerator.Current.Error!.Message, StringComparison.OrdinalIgnoreCase);

            Assert.False(await enumerator.MoveNextAsync());
            await enumerator.DisposeAsync();
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_ClientCancellationPropagatesToServer()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat");
        options.AddDuplexOutbound("chat", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var serverCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::client-cancel",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                cancellationToken.Register(() => serverCancelled.TrySetResult(true));

                var pump = Task.Run(async () =>
                {
                    try
                    {
                        var handshake = codec.EncodeResponse(new ChatMessage("ready"), call.ResponseMeta);
                        if (handshake.IsFailure)
                        {
                            await call.CompleteResponsesAsync(handshake.Error!, cancellationToken);
                            return;
                        }

                        await call.ResponseWriter.WriteAsync(handshake.Value, cancellationToken);

                        await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken))
                        {
                            var decode = codec.DecodeRequest(payload, request.Meta);
                            if (decode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(decode.Error!, cancellationToken);
                                return;
                            }

                            var response = new ChatMessage($"ack:{decode.Value.Message}");
                            var encode = codec.EncodeResponse(response, call.ResponseMeta);
                            if (encode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(encode.Error!, cancellationToken);
                                return;
                            }

                            await call.ResponseWriter.WriteAsync(encode.Value, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        serverCancelled.TrySetResult(true);
                    }
                    finally
                    {
                        await call.CompleteResponsesAsync(cancellationToken: cancellationToken);
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateDuplexStreamClient("chat", codec);
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::client-cancel",
                encoding: "application/json",
                transport: "grpc");

            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var sessionResult = await client.StartAsync(requestMeta, callCts.Token);
            await using var session = sessionResult.ValueOrThrow();
            var writeResult = await session.WriteAsync(new ChatMessage("hello"), ct);
            writeResult.ThrowIfFailure();

            await callCts.CancelAsync();

            await using var enumerator = session.ReadResponsesAsync(ct).GetAsyncEnumerator(ct);
            var moveNextTask = enumerator.MoveNextAsync().AsTask();
            await moveNextTask.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(moveNextTask.Result);
            Assert.True(enumerator.Current.IsFailure);
            Assert.Equal(OmniRelayStatusCode.Cancelled, OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!));
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_FlowControl_ServerSlow()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat");
        options.AddDuplexOutbound("chat", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::flow",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                var pump = Task.Run(async () =>
                {
                    var index = 0;
                    try
                    {
                        var handshake = codec.EncodeResponse(new ChatMessage("ready"), call.ResponseMeta);
                        if (handshake.IsFailure)
                        {
                            await call.CompleteResponsesAsync(handshake.Error!, cancellationToken);
                            return;
                        }

                        await call.ResponseWriter.WriteAsync(handshake.Value, cancellationToken);

                        await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken))
                        {
                            index++;
                            await Task.Delay(15, cancellationToken);

                            var decode = codec.DecodeRequest(payload, request.Meta);
                            if (decode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(decode.Error!, cancellationToken);
                                return;
                            }

                            var response = new ChatMessage($"ack-{index}:{decode.Value.Message}");
                            var encode = codec.EncodeResponse(response, call.ResponseMeta);
                            if (encode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(encode.Error!, cancellationToken);
                                return;
                            }

                            await call.ResponseWriter.WriteAsync(encode.Value, cancellationToken);
                        }

                        await call.CompleteResponsesAsync(cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        await call.CompleteResponsesAsync(OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Internal,
                            ex.Message ?? "flow-control failure",
                            transport: TransportName), cancellationToken);
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateDuplexStreamClient("chat", codec);
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::flow",
                encoding: "application/json",
                transport: "grpc");

            var sessionResult = await client.StartAsync(requestMeta, ct);
            await using var session = sessionResult.ValueOrThrow();

            const int messageCount = 10;
            for (var i = 0; i < messageCount; i++)
            {
                var writeResult = await session.WriteAsync(new ChatMessage($"msg-{i}"), ct);
                writeResult.ThrowIfFailure();
            }

            await session.CompleteRequestsAsync(cancellationToken: ct);

            var responses = new List<string>();
            await foreach (var response in session.ReadResponsesAsync(ct))
            {
                responses.Add(response.ValueOrThrow().Body.Message);
            }

            Assert.Equal(messageCount + 1, responses.Count);
            Assert.Equal("ready", responses[0]);
            for (var i = 0; i < messageCount; i++)
            {
                Assert.Equal($"ack-{i + 1}:msg-{i}", responses[i + 1]);
            }
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Unary_PropagatesMetadataBetweenClientAndServer()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "echo");
        options.AddUnaryOutbound("echo", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var observedMeta = new TaskCompletionSource<RequestMeta>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "metadata",
            (request, cancellationToken) =>
            {
                observedMeta.TrySetResult(request.Meta);

                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!));
                }

                var responseMessage = new EchoResponse { Message = decodeResult.Value.Message };
                var responseMeta = new ResponseMeta(encoding: "application/json").WithHeader("x-response-id", "42");
                var encodeResult = codec.EncodeResponse(responseMessage, responseMeta);
                if (encodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!));
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, responseMeta);
                return ValueTask.FromResult(Ok(response));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateUnaryClient("echo", codec);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            var ttl = TimeSpan.FromSeconds(5);

            var requestMeta = new RequestMeta(
                service: "echo",
                procedure: "metadata",
                caller: "caller-123",
                encoding: "application/json",
                transport: "grpc",
                shardKey: "shard-1",
                routingKey: "route-1",
                routingDelegate: "delegate-1",
                timeToLive: ttl,
                deadline: deadline,
                headers:
                [
                    new KeyValuePair<string, string>("x-trace-id", "trace-abc"),
                    new KeyValuePair<string, string>("x-feature", "beta")
                ]);

            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("payload"));

            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsSuccess, result.Error?.Message);

            var serverMeta = await observedMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(requestMeta.Caller, serverMeta.Caller);
            Assert.Equal(requestMeta.ShardKey, serverMeta.ShardKey);
            Assert.Equal(requestMeta.RoutingKey, serverMeta.RoutingKey);
            Assert.Equal(requestMeta.RoutingDelegate, serverMeta.RoutingDelegate);
            Assert.Equal(requestMeta.TimeToLive, serverMeta.TimeToLive);

            Assert.True(serverMeta.Deadline.HasValue);
            Assert.InRange((serverMeta.Deadline.Value - deadline).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(5));

            Assert.Equal("trace-abc", serverMeta.Headers["x-trace-id"]);
            Assert.Equal("beta", serverMeta.Headers["x-feature"]);

            var responseMeta = result.Value.Meta;
            Assert.Equal("application/json", responseMeta.Encoding);
            Assert.True(responseMeta.TryGetHeader("x-response-id", out var responseId));
            Assert.Equal("42", responseId);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerStreaming_PropagatesMetadataAndHeaders()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var observedMeta = new TaskCompletionSource<RequestMeta>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new StreamProcedureSpec(
            "stream",
            "stream::meta",
            (request, callOptions, cancellationToken) =>
            {
                observedMeta.TrySetResult(request.Meta);

                var streamCall = GrpcServerStreamCall.Create(
                    request.Meta,
                    new ResponseMeta(encoding: "application/json").WithHeader("x-stream-id", "stream-99"));

                var background = Task.Run(async () =>
                {
                    try
                    {
                        var encodeResult = codec.EncodeResponse(new EchoResponse { Message = "first" }, streamCall.ResponseMeta);
                        if (encodeResult.IsFailure)
                        {
                            await streamCall.CompleteAsync(encodeResult.Error!, cancellationToken);
                            return;
                        }

                        await streamCall.WriteAsync(encodeResult.Value, cancellationToken);

                        var secondEncode = codec.EncodeResponse(new EchoResponse { Message = "second" }, streamCall.ResponseMeta);
                        if (secondEncode.IsFailure)
                        {
                            await streamCall.CompleteAsync(secondEncode.Error!, cancellationToken);
                            return;
                        }

                        await streamCall.WriteAsync(secondEncode.Value, cancellationToken);
                    }
                    finally
                    {
                        await streamCall.CompleteAsync();
                    }
                }, cancellationToken);

                serverTasks.Track(background);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateStreamClient("stream", codec);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);

            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::meta",
                caller: "stream-caller",
                encoding: "application/json",
                transport: "grpc",
                timeToLive: TimeSpan.FromSeconds(10),
                deadline: deadline,
                headers:
                [
                    new KeyValuePair<string, string>("x-meta", "value")
                ]);

            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("ignored"));

            var responses = new List<Response<EchoResponse>>();
            await foreach (var response in client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct))
            {
                responses.Add(response.ValueOrThrow());
            }

            Assert.Equal(2, responses.Count);
            Assert.Equal("first", responses[0].Body.Message);
            Assert.Equal("second", responses[1].Body.Message);

            var serverMeta = await observedMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("stream-caller", serverMeta.Caller);
            Assert.Equal(TimeSpan.FromSeconds(10), serverMeta.TimeToLive);
            Assert.True(serverMeta.Deadline.HasValue);
            Assert.InRange((serverMeta.Deadline.Value - deadline).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
            Assert.Equal("value", serverMeta.Headers["x-meta"]);

            foreach (var response in responses)
            {
                Assert.Equal("application/json", response.Meta.Encoding);
                Assert.True(response.Meta.TryGetHeader("x-stream-id", out var streamId));
                Assert.Equal("stream-99", streamId);
            }
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcTransport_ResponseTrailers_SurfaceEncodingAndStatus()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("meta");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "meta",
            "success",
            (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!));
                }

                var responseMeta = new ResponseMeta(encoding: "application/json").WithHeader("x-meta-response", "yes");
                var encodeResult = codec.EncodeResponse(new EchoResponse { Message = decodeResult.Value.Message }, responseMeta);
                if (encodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!));
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, responseMeta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            });

            var method = new Method<byte[], byte[]>(
                MethodType.Unary,
                "meta",
                "success",
                Marshallers.Create(static payload => payload ?? [], static payload => payload ?? []),
                Marshallers.Create(static payload => payload ?? [], static payload => payload ?? []));

            var metadata = new Metadata
            {
                { EncodingHeaderKey, "application/json" }
            };

            var payload = JsonSerializer.SerializeToUtf8Bytes(new EchoRequest("hello"));
            var call = channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(metadata, cancellationToken: ct), payload);

            var responseBytes = await call.ResponseAsync;
            var headers = await call.ResponseHeadersAsync;
            var trailers = call.GetTrailers();

            Assert.NotEmpty(responseBytes);

            bool encodingHeaderFound = headers.Any(entry => string.Equals(entry.Key, EncodingTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "application/json", StringComparison.OrdinalIgnoreCase))
                || trailers.Any(entry => string.Equals(entry.Key, EncodingTrailerKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value, "application/json", StringComparison.OrdinalIgnoreCase));
            Assert.True(encodingHeaderFound, "Expected OmniRelay encoding metadata to be present in headers or trailers.");

            bool customHeaderFound = headers.Any(entry => string.Equals(entry.Key, "x-meta-response", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "yes", StringComparison.OrdinalIgnoreCase))
                || trailers.Any(entry => string.Equals(entry.Key, "x-meta-response", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value, "yes", StringComparison.OrdinalIgnoreCase));
            Assert.True(customHeaderFound, "Expected custom response header to be present in headers or trailers.");

        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcTransport_ResponseTrailers_SurfaceErrorMetadata()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("meta");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            "meta",
            "fail",
            (request, cancellationToken) =>
            {
                var error = OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.PermissionDenied,
                    "access denied",
                    transport: TransportName);
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            });

            var method = new Method<byte[], byte[]>(
                MethodType.Unary,
                "meta",
                "fail",
                Marshallers.Create(static payload => payload ?? [], static payload => payload ?? []),
                Marshallers.Create(static payload => payload ?? [], static payload => payload ?? []));

            var metadata = new Metadata();
            var payload = Array.Empty<byte>();

            var call = channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(metadata, cancellationToken: ct), payload);

            var rpcException = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
            Assert.Equal(StatusCode.PermissionDenied, rpcException.StatusCode);

            var trailers = rpcException.Trailers;
            Assert.Contains(trailers, entry => string.Equals(entry.Key, ErrorMessageTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "access denied", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(trailers, entry => string.Equals(entry.Key, ErrorCodeTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "permission-denied", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(trailers, entry => string.Equals(entry.Key, StatusTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, nameof(OmniRelayStatusCode.PermissionDenied), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    public static IEnumerable<object[]> FromStatusMappings()
    {
        yield return [StatusCode.Cancelled, OmniRelayStatusCode.Cancelled];
        yield return [StatusCode.InvalidArgument, OmniRelayStatusCode.InvalidArgument];
        yield return [StatusCode.DeadlineExceeded, OmniRelayStatusCode.DeadlineExceeded];
        yield return [StatusCode.NotFound, OmniRelayStatusCode.NotFound];
        yield return [StatusCode.AlreadyExists, OmniRelayStatusCode.AlreadyExists];
        yield return [StatusCode.PermissionDenied, OmniRelayStatusCode.PermissionDenied];
        yield return [StatusCode.ResourceExhausted, OmniRelayStatusCode.ResourceExhausted];
        yield return [StatusCode.FailedPrecondition, OmniRelayStatusCode.FailedPrecondition];
        yield return [StatusCode.Aborted, OmniRelayStatusCode.Aborted];
        yield return [StatusCode.OutOfRange, OmniRelayStatusCode.OutOfRange];
        yield return [StatusCode.Unimplemented, OmniRelayStatusCode.Unimplemented];
        yield return [StatusCode.Internal, OmniRelayStatusCode.Internal];
        yield return [StatusCode.Unavailable, OmniRelayStatusCode.Unavailable];
        yield return [StatusCode.DataLoss, OmniRelayStatusCode.DataLoss];
        yield return [StatusCode.Unknown, OmniRelayStatusCode.Unknown];
        yield return [StatusCode.Unauthenticated, OmniRelayStatusCode.PermissionDenied];
    }

    public static IEnumerable<object[]> ToStatusMappings()
    {
        yield return [StatusCode.Cancelled, OmniRelayStatusCode.Cancelled];
        yield return [StatusCode.InvalidArgument, OmniRelayStatusCode.InvalidArgument];
        yield return [StatusCode.DeadlineExceeded, OmniRelayStatusCode.DeadlineExceeded];
        yield return [StatusCode.NotFound, OmniRelayStatusCode.NotFound];
        yield return [StatusCode.AlreadyExists, OmniRelayStatusCode.AlreadyExists];
        yield return [StatusCode.PermissionDenied, OmniRelayStatusCode.PermissionDenied];
        yield return [StatusCode.ResourceExhausted, OmniRelayStatusCode.ResourceExhausted];
        yield return [StatusCode.FailedPrecondition, OmniRelayStatusCode.FailedPrecondition];
        yield return [StatusCode.Aborted, OmniRelayStatusCode.Aborted];
        yield return [StatusCode.OutOfRange, OmniRelayStatusCode.OutOfRange];
        yield return [StatusCode.Unimplemented, OmniRelayStatusCode.Unimplemented];
        yield return [StatusCode.Internal, OmniRelayStatusCode.Internal];
        yield return [StatusCode.Unavailable, OmniRelayStatusCode.Unavailable];
        yield return [StatusCode.DataLoss, OmniRelayStatusCode.DataLoss];
        yield return [StatusCode.Unknown, OmniRelayStatusCode.Unknown];
    }

    [Theory]
    [MemberData(nameof(FromStatusMappings))]
    public void GrpcStatusMapper_FromStatus_MapsExpected(StatusCode statusCode, OmniRelayStatusCode expected)
    {
        var mapperType = typeof(GrpcOutbound).Assembly.GetType("OmniRelay.Transport.Grpc.GrpcStatusMapper", throwOnError: true)
            ?? throw new InvalidOperationException("Unable to locate GrpcStatusMapper type.");
        var method = mapperType.GetMethod("FromStatus", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate FromStatus method.");

        var status = new Status(statusCode, "detail");
        var result = (OmniRelayStatusCode)method.Invoke(null, [status])!;

        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(ToStatusMappings))]
    public void GrpcStatusMapper_ToStatus_MapsExpected(StatusCode expectedStatusCode, OmniRelayStatusCode polymerStatus)
    {
        var mapperType = typeof(GrpcOutbound).Assembly.GetType("OmniRelay.Transport.Grpc.GrpcStatusMapper", throwOnError: true)
            ?? throw new InvalidOperationException("Unable to locate GrpcStatusMapper type.");
        var method = mapperType.GetMethod("ToStatus", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate ToStatus method.");

        var status = (Status)method.Invoke(null, [polymerStatus, "detail"])!;

        Assert.Equal(expectedStatusCode, status.StatusCode);
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcOutbound_TelemetryOptions_EnableClientLoggingInterceptor()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound([address.ToString()], telemetryOptions: new GrpcTelemetryOptions { EnableServerLogging = false });
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "echo::ping",
            (request, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decode.Error!));
                }

                var responseMeta = new ResponseMeta(encoding: "application/json");
                var encode = codec.EncodeResponse(new EchoResponse { Message = decode.Value.Message }, responseMeta);
                return encode.IsSuccess
                    ? ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta)))
                    : ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var loggerProvider = new CaptureLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(loggerProvider);
        });

        var outbound = new GrpcOutbound(
            address,
            "echo",
            telemetryOptions: new GrpcTelemetryOptions
            {
                EnableClientLogging = true,
                LoggerFactory = loggerFactory
            });

        await outbound.StartAsync(ct);

        try
        {
            var requestMeta = new RequestMeta(
                service: "echo",
                procedure: "echo::ping",
                encoding: "application/json",
                transport: TransportName);
            var payload = codec.EncodeRequest(new EchoRequest("hello"), requestMeta);
            Assert.True(payload.IsSuccess);

            var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload.Value);
            var responseResult = await outbound.CallAsync(request, ct);
            Assert.True(responseResult.IsSuccess, responseResult.Error?.Message);

            Assert.Contains(loggerProvider.Entries, entry =>
                string.Equals(entry.CategoryName, typeof(GrpcClientLoggingInterceptor).FullName, StringComparison.Ordinal) &&
                entry.Message.Contains("Completed gRPC client unary call", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcInbound_ServerLoggingInterceptor_WritesLogs()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var loggerProvider = new CaptureLoggerProvider();

        var options = new DispatcherOptions("logging");
        var grpcInbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.AddProvider(loggerProvider);
                    builder.SetMinimumLevel(LogLevel.Information);
                });
            },
            telemetryOptions: new GrpcTelemetryOptions { EnableServerLogging = true });
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "logging",
            "logging::ping",
            (request, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decode.Error!));
                }

                var responseMeta = new ResponseMeta(encoding: "application/json");
                var encode = codec.EncodeResponse(new EchoResponse { Message = decode.Value.Message }, responseMeta);
                return encode.IsSuccess
                    ? ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta)))
                    : ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var outbound = new GrpcOutbound(address, "logging");
        await outbound.StartAsync(ct);

        try
        {
            var requestMeta = new RequestMeta(
                service: "logging",
                procedure: "logging::ping",
                encoding: "application/json",
                transport: TransportName);
            var payload = codec.EncodeRequest(new EchoRequest("hello"), requestMeta);
            Assert.True(payload.IsSuccess);

            var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload.Value);
            var responseResult = await outbound.CallAsync(request, ct);
            Assert.True(responseResult.IsSuccess, responseResult.Error?.Message);
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }

        Assert.Contains(loggerProvider.Entries, entry =>
            string.Equals(entry.CategoryName, typeof(GrpcServerLoggingInterceptor).FullName, StringComparison.Ordinal) &&
            entry.Message.Contains("Completed gRPC server unary call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcInbound_TelemetryOptions_RegistersServerLoggingInterceptor()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        double? serverDuration = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, GrpcTransportMetrics.MeterName, StringComparison.Ordinal) &&
                    string.Equals(instrument.Name, "omnirelay.grpc.server.unary.duration", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (string.Equals(instrument.Name, "omnirelay.grpc.server.unary.duration", StringComparison.Ordinal))
            {
                serverDuration = measurement;
            }
        });
        listener.Start();

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound([address.ToString()], telemetryOptions: new GrpcTelemetryOptions());
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "echo::ping",
            (request, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decode.Error!));
                }

                var responseMeta = new ResponseMeta(encoding: "application/json");
                var encode = codec.EncodeResponse(new EchoResponse { Message = decode.Value.Message }, responseMeta);
                return encode.IsSuccess
                    ? ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta)))
                    : ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var outbound = new GrpcOutbound(address, "echo");
        await outbound.StartAsync(ct);

        try
        {
            var requestMeta = new RequestMeta(
                service: "echo",
                procedure: "echo::ping",
                encoding: "application/json",
                transport: TransportName);
            var payload = codec.EncodeRequest(new EchoRequest("hello"), requestMeta);
            Assert.True(payload.IsSuccess);

            var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload.Value);
            var responseResult = await outbound.CallAsync(request, ct);
            Assert.True(responseResult.IsSuccess, responseResult.Error?.Message);
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }

        Assert.True(serverDuration.HasValue && serverDuration.Value > 0, "Expected server unary duration metric to be recorded.");
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentBag<LogEntry> _entries = [];

        public IReadOnlyCollection<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        internal sealed record LogEntry(string CategoryName, LogLevel LogLevel, string Message)
        {
            public string CategoryName
            {
                get => field;
                init => field = value;
            } = CategoryName;

            public LogLevel LogLevel
            {
                get => field;
                init => field = value;
            } = LogLevel;

            public string Message
            {
                get => field;
                init => field = value;
            } = Message;
        }

        private sealed class CaptureLogger(string categoryName, ConcurrentBag<LogEntry> entries) : ILogger
        {
            private readonly string _categoryName = categoryName;
            private readonly ConcurrentBag<LogEntry> _entries = entries;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                var message = formatter(state, exception) ?? string.Empty;
                _entries.Add(new LogEntry(_categoryName, logLevel, message));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        const int maxAttempts = 100;
        const int connectTimeoutMilliseconds = 200;
        const int settleDelayMilliseconds = 50;
        const int retryDelayMilliseconds = 20;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port)
                            .WaitAsync(TimeSpan.FromMilliseconds(connectTimeoutMilliseconds), cancellationToken);

                await Task.Delay(TimeSpan.FromMilliseconds(settleDelayMilliseconds), cancellationToken);
                return;
            }
            catch (SocketException)
            {
                // Listener not ready yet; retry.
            }
            catch (TimeoutException)
            {
                // Connection attempt timed out; retry.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
    }


    private sealed class ServerTaskTracker : IAsyncDisposable
    {
        private readonly List<Task> _tasks = [];

        public void Track(Task task)
        {
            if (task is null)
            {
                return;
            }

            lock (_tasks)
            {
                _tasks.Add(task);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task[] toAwait;
            lock (_tasks)
            {
                if (_tasks.Count == 0)
                {
                    return;
                }

                toAwait = [.. _tasks];
                _tasks.Clear();
            }

            try
            {
                await Task.WhenAll(toAwait);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation tokens propagate during shutdown.
            }
        }
    }

    private sealed class RecordingClientInterceptor : Interceptor
    {
        private int _unaryCount;

        public int UnaryCallCount => Volatile.Read(ref _unaryCount);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            Interlocked.Increment(ref _unaryCount);

            var headers = context.Options.Headers ?? [];
            headers.Add("x-client-interceptor", "true");

            var options = context.Options.WithHeaders(headers);
            var newContext = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options);
            return continuation(request, newContext);
        }
    }

    private sealed class RecordingServerInterceptor : Interceptor
    {
        private int _unaryCount;

        public int UnaryCallCount => Volatile.Read(ref _unaryCount);

        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            Interlocked.Increment(ref _unaryCount);
            return continuation(request, context);
        }
    }

    private sealed record EchoRequest(string Message)
    {
        public string Message
        {
            get => field;
            init => field = value;
        } = Message;
    }

    private sealed record EchoResponse
    {
        public string Message
        {
            get => field;
            init => field = value;
        } = string.Empty;
    }

    private sealed record AggregateChunk(int Amount)
    {
        public int Amount
        {
            get => field;
            init => field = value;
        } = Amount;
    }

    private sealed record AggregateResponse(int TotalAmount)
    {
        public int TotalAmount
        {
            get => field;
            init => field = value;
        } = TotalAmount;
    }

    private sealed record ChatMessage(string Message)
    {
        public string Message
        {
            get => field;
            init => field = value;
        } = Message;
    }

    private sealed class DummyCompressionProvider : ICompressionProvider
    {
        public DummyCompressionProvider(string encodingName)
        {
            if (string.IsNullOrWhiteSpace(encodingName))
            {
                throw new ArgumentException("Encoding name is required.", nameof(encodingName));
            }

            EncodingName = encodingName;
        }

        public string EncodingName => field;

        public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel) => stream;

        public Stream CreateDecompressionStream(Stream stream) => stream;
    }
}
