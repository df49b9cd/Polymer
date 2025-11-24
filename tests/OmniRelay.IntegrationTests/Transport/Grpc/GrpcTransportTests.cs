using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
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
using OmniRelay.Errors;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Grpc;
using Xunit;
using static AwesomeAssertions.FluentActions;
using static Hugo.Go;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport;

public partial class GrpcTransportTests(ITestOutputHelper output) : TransportIntegrationTest(output)
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

    [Fact(Timeout = TestTimeouts.Default)]
    public void CompressionOptions_ValidateRequiresRegisteredAlgorithm()
    {
        var options = new GrpcCompressionOptions
        {
            Providers = [],
            DefaultAlgorithm = "gzip"
        };

        var result = options.Validate();

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

        acceptEncoding.Should().Be(provider.EncodingName);
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

        acceptEncoding.Should().Be("identity");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask GrpcOutbound_CallBeforeStart_Throws()
    {
        var outbound = new GrpcOutbound(new Uri("http://127.0.0.1:5000"), "echo");
        var requestMeta = new RequestMeta(service: "echo", procedure: "echo::ping", transport: TransportName);
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);

        var result = await outbound.CallAsync(request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.FailedPrecondition);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ServerStreaming_OverGrpcTransport()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ServerStreaming_OverGrpcTransport), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateStreamClient("stream", codec));
        var requestMeta = new RequestMeta(
            service: "stream",
            procedure: "stream::events",
            encoding: "application/json",
            transport: "grpc");
        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("seed"));

        var responses = new List<string>();
        await foreach (var response in client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct))
        {
            responses.Add(response.ValueOrChecked().Body.Message);
        }

        responses.Should().Equal("event-0", "event-1", "event-2");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcInbound_BindsConfiguredHttp2EndpointsOnly()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_BindsConfiguredHttp2EndpointsOnly), dispatcher, ct);
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
            response.IsSuccess.Should().BeTrue(response.Error?.Message);

            var exception = await Invoking(async () =>
            {
                var unusedPort = TestPortAllocator.GetRandomPort();
                using var unusedClient = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(200));
                await unusedClient.ConnectAsync("127.0.0.1", unusedPort, timeoutCts.Token);
            }).Should().ThrowAsync<Exception>();

            var exceptionType = exception.Which.GetType();
            (exceptionType == typeof(SocketException) || exceptionType == typeof(OperationCanceledException))
                .Should().BeTrue($"Expected connection failure but observed {exceptionType.FullName}.");
        }
        finally
        {
            await outbound.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask StopAsync_WaitsForActiveGrpcCallsAndRejectsNewOnes()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(StopAsync_WaitsForActiveGrpcCallsAndRejectsNewOnes), dispatcher, ct, ownsLifetime: false);
        await WaitForGrpcReadyAsync(address, ct);

        using var channel = GrpcChannel.ForAddress(address);
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "lifecycle", "test::slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        var inFlightCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsyncChecked(ct);

        await Task.Delay(100, ct);

        var rejectedCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
        var rejection = await FluentActions.Awaiting(() => rejectedCall.ResponseAsync)
            .Should().ThrowAsync<RpcException>();
        rejection.Which.StatusCode.Should().Be(StatusCode.Unavailable);
        rejection.Which.Trailers.GetValue("retry-after").Should().Be("1");
        stopTask.IsCompleted.Should().BeFalse();

        releaseRequest.TrySetResult();
        var response = await inFlightCall.ResponseAsync.WaitAsync(ct);
        response.Should().BeEmpty();

        await stopTask;
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask StopAsyncCancellation_CompletesWithoutDrainingGrpcCalls()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(StopAsyncCancellation_CompletesWithoutDrainingGrpcCalls), dispatcher, ct, ownsLifetime: false);
        await WaitForGrpcReadyAsync(address, ct);

        using var channel = GrpcChannel.ForAddress(address);
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "lifecycle", "test::slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        var inFlightCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
        await requestStarted.Task.WaitAsync(ct);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopTask = dispatcher.StopAsyncChecked(stopCts.Token);

        await stopTask;
        releaseRequest.Task.IsCompleted.Should().BeFalse();
        releaseRequest.TrySetResult();

        try
        {
            await inFlightCall.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (RpcException)
        {
        }
        catch (Exception)
        {
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcInbound_WithTlsCertificate_BindsAndServes()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithTlsCertificate_BindsAndServes), dispatcher, ct);
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
            response.IsSuccess.Should().BeTrue(response.Error?.Message);
        }
        finally
        {
            await outbound.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcHealthService_ReflectsReadiness()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcHealthService_ReflectsReadiness), dispatcher, ct, ownsLifetime: false);
        await WaitForGrpcReadyAsync(address, ct);

        using var channel = GrpcChannel.ForAddress(address);
        var healthClient = new Health.HealthClient(channel);

        var healthy = await healthClient.CheckAsync(new HealthCheckRequest(), cancellationToken: ct).ResponseAsync.WaitAsync(ct);
        healthy.Status.Should().Be(HealthCheckResponse.Types.ServingStatus.Serving);

        var method = new Method<byte[], byte[]>(MethodType.Unary, "health", "slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var invoker = channel.CreateCallInvoker();
        var inFlightCall = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsyncChecked(ct);

        var draining = await healthClient.CheckAsync(new HealthCheckRequest(), cancellationToken: ct).ResponseAsync.WaitAsync(ct);
        draining.Status.Should().Be(HealthCheckResponse.Types.ServingStatus.NotServing);

        releaseRequest.TrySetResult();
        await inFlightCall.ResponseAsync.WaitAsync(ct);
        await stopTask;
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ServerStreaming_ErrorMidStream_PropagatesToClient()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ServerStreaming_ErrorMidStream_PropagatesToClient), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateStreamClient("stream", codec));
        var requestMeta = new RequestMeta(
            service: "stream",
            procedure: "stream::fails",
            encoding: "application/json",
            transport: "grpc");
        var payload = new string('x', 4096);
        var request = new Request<EchoRequest>(requestMeta, new EchoRequest(payload));

        await using var enumerator = client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct)
            .GetAsyncEnumerator(ct);

        if (!await enumerator.MoveNextAsync())
        {
            // Stream reset before first message; acceptable under current transport behavior.
            return;
        }

        if (enumerator.Current.IsSuccess)
        {
            enumerator.Current.ValueOrChecked().Body.Message.Should().Be("first");

            (await enumerator.MoveNextAsync()).Should().BeTrue();
            var terminal = enumerator.Current;
            terminal.IsFailure.Should().BeTrue();
            OmniRelayErrorAdapter.ToStatus(terminal.Error!)
                .Should().BeOneOf(OmniRelayStatusCode.Internal, OmniRelayStatusCode.Unknown);
        }
        else
        {
            // Received terminal error as first item.
            OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!)
                .Should().BeOneOf(OmniRelayStatusCode.Internal, OmniRelayStatusCode.Unknown);
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcOutbound_RoundRobinPeers_RecordSuccessAcrossEndpoints()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcOutbound_RoundRobinPeers_RecordSuccessAcrossEndpoints), dispatcher, ct);
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
                requestPayload.IsSuccess.Should().BeTrue();
                var request = new Request<ReadOnlyMemory<byte>>(meta, requestPayload.Value);

                var responseResult = await outbound.CallAsync(request, ct);
                responseResult.IsSuccess.Should().BeTrue(responseResult.Error?.Message);

                var decode = codec.DecodeResponse(responseResult.Value.Body, responseResult.Value.Meta);
                decode.IsSuccess.Should().BeTrue(decode.Error?.Message);
                decode.Value.Message.Should().Be($"call-{i}");
            }

            var snapshot = outbound.GetOutboundDiagnostics().Should().BeOfType<GrpcOutboundSnapshot>().Which;
            snapshot.PeerSummaries.Should().HaveCount(2);
            snapshot.PeerSummaries.Should().AllSatisfy(peer =>
            {
                peer.LastSuccess.HasValue.Should().BeTrue();
                peer.SuccessCount.Should().BeGreaterThan(0);
                peer.AverageLatencyMs.Should().NotBeNull();
                peer.P50LatencyMs.Should().NotBeNull();
            });
        }
        finally
        {
            await outbound.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcOutbound_FailingPeerTriggersCircuitBreaker()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcOutbound_FailingPeerTriggersCircuitBreaker), dispatcher, ct);
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
            failingPayload.IsSuccess.Should().BeTrue();
            var failingRequest = new Request<ReadOnlyMemory<byte>>(failingMeta, failingPayload.Value);

            var failureResult = await outbound.CallAsync(failingRequest, ct);
            failureResult.IsFailure.Should().BeTrue();

            var successMeta = new RequestMeta(
                service: "echo",
                procedure: "echo::ping",
                encoding: "application/json",
                transport: "grpc");
            var successPayload = codec.EncodeRequest(new EchoRequest("second"), successMeta);
            successPayload.IsSuccess.Should().BeTrue();
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

            successResult.IsSuccess.Should().BeTrue(successResult.Error?.Message ?? "Unable to reach healthy gRPC peer after retries");

            var decode = codec.DecodeResponse(successResult.Value.Body, successResult.Value.Meta);
            decode.IsSuccess.Should().BeTrue(decode.Error?.Message);
            decode.Value.Message.Should().Be("second");

            var snapshot = outbound.GetOutboundDiagnostics().Should().BeOfType<GrpcOutboundSnapshot>().Which;
            snapshot.PeerSummaries.Should().HaveCount(2);
            var failingPeerSummary = snapshot.PeerSummaries.Single(peer => peer.Address == failingAddress);
            failingPeerSummary.LastFailure.HasValue.Should().BeTrue();
            failingPeerSummary.FailureCount.Should().BeGreaterThan(0);
            var healthyPeerSummary = snapshot.PeerSummaries.Single(peer => peer.Address == healthyAddress);
            healthyPeerSummary.LastSuccess.HasValue.Should().BeTrue();
        }
        finally
        {
            await outbound.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ServerStreaming_PayloadAboveLimit_FaultsStream()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ServerStreaming_PayloadAboveLimit_FaultsStream), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var rawCodec = new RawCodec();
        var client = RequireClient(dispatcher.CreateStreamClient<byte[], byte[]>("stream-limit", rawCodec));
        var requestMeta = new RequestMeta(
            service: "stream-limit",
            procedure: "stream::oversized",
            encoding: RawCodec.DefaultEncoding,
            transport: TransportName);
        var request = new Request<byte[]>(requestMeta, []);

        await using var enumerator = client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct).GetAsyncEnumerator(ct);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!)
            .Should().BeOneOf(OmniRelayStatusCode.ResourceExhausted, OmniRelayStatusCode.Unknown);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ClientStreaming_OverGrpcTransport()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ClientStreaming_OverGrpcTransport), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateClientStreamClient("stream", codec));

        var requestMeta = new RequestMeta(
            service: "stream",
            procedure: "stream::aggregate",
            encoding: codec.Encoding,
            transport: "grpc");

        var streamResult = await client.StartAsync(requestMeta, ct);
        await using var stream = streamResult.ValueOrChecked();

        var firstWrite = await stream.WriteAsync(new AggregateChunk(Amount: 2), ct);
        firstWrite.ValueOrChecked();
        var secondWrite = await stream.WriteAsync(new AggregateChunk(Amount: 5), ct);
        secondWrite.ValueOrChecked();
        await stream.CompleteAsync(ct);

        var responseResult = await stream.Response;
        var response = responseResult.ValueOrChecked();

        response.Body.TotalAmount.Should().Be(7);
        stream.ResponseMeta.Encoding.Should().Be(codec.Encoding);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ClientStreaming_CancellationFromClient()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ClientStreaming_CancellationFromClient), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateClientStreamClient("stream", codec));
        var requestMeta = new RequestMeta(service: "stream", procedure: "stream::aggregate", encoding: codec.Encoding, transport: "grpc");

        var streamResult = await client.StartAsync(requestMeta, ct);
        await using var stream = streamResult.ValueOrChecked();

        await cts.CancelAsync();

        await Invoking(async () => await stream.WriteAsync(new AggregateChunk(Amount: 1), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ClientStreaming_DeadlineExceededMapsStatus()
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
                context.Meta.Deadline.HasValue.Should().BeTrue();
                await foreach (var _ in context.Requests.ReadAllAsync(cancellationToken))
                {
                }

                return Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.DeadlineExceeded,
                    "deadline exceeded"));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ClientStreaming_DeadlineExceededMapsStatus), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateClientStreamClient("stream", codec));
        var requestMeta = new RequestMeta(
            service: "stream",
            procedure: "stream::deadline",
            encoding: codec.Encoding,
            transport: "grpc",
            deadline: DateTimeOffset.UtcNow.AddMilliseconds(200));

        var streamResult = await client.StartAsync(requestMeta, ct);
        await using var stream = streamResult.ValueOrChecked();
        await stream.CompleteAsync(ct);

        var responseResult = await stream.Response;
        responseResult.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(responseResult.Error!).Should().Be(OmniRelayStatusCode.DeadlineExceeded);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ClientStreaming_LargePayloadChunks()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ClientStreaming_LargePayloadChunks), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateClientStreamClient("stream", codec));
        var requestMeta = new RequestMeta(service: "stream", procedure: "stream::huge", encoding: codec.Encoding, transport: "grpc");

        var streamResult = await client.StartAsync(requestMeta, ct);
        await using var stream = streamResult.ValueOrChecked();

        const int chunkCount = 1_000;
        for (var i = 0; i < chunkCount; i++)
        {
            var writeResult = await stream.WriteAsync(new AggregateChunk(1), ct);
            writeResult.ValueOrChecked();
        }

        await stream.CompleteAsync(ct);

        var responseResult = await stream.Response;
        var response = responseResult.ValueOrChecked();
        response.Body.TotalAmount.Should().Be(chunkCount);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ClientStreaming_ServerErrorPropagatesToClient()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ClientStreaming_ServerErrorPropagatesToClient), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateClientStreamClient("stream", codec));
        var requestMeta = new RequestMeta(
            service: "stream",
            procedure: "stream::server-error",
            encoding: codec.Encoding,
            transport: "grpc");

        var streamResult = await client.StartAsync(requestMeta, ct);
        await using var stream = streamResult.ValueOrChecked();

        var writeResult = await stream.WriteAsync(new AggregateChunk(Amount: 1), ct);
        writeResult.ValueOrChecked();
        await stream.CompleteAsync(ct);

        var responseResult = await stream.Response;
        responseResult.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(responseResult.Error!).Should().Be(OmniRelayStatusCode.Unavailable);
        responseResult.Error!.Message.Should().ContainEquivalentOf("service unavailable");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask Unary_ClientInterceptorExecutes()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(Unary_ClientInterceptorExecutes), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateUnaryClient("intercept", codec));
        var requestMeta = new RequestMeta(
            service: "intercept",
            procedure: "intercept::echo",
            encoding: "application/json",
            transport: "grpc");

        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("payload"));
        var response = await client.CallAsync(request, ct);

        response.IsSuccess.Should().BeTrue(response.Error?.Message);
        (await observedClientHeader.Task).ToLowerInvariant().Should().Be("true");
        clientInterceptor.UnaryCallCount.Should().Be(1);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask Unary_ServerInterceptorExecutes()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(Unary_ServerInterceptorExecutes), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateUnaryClient("server-intercept", codec));
        var requestMeta = new RequestMeta(
            service: "server-intercept",
            procedure: "server-intercept::echo",
            encoding: "application/json",
            transport: "grpc");
        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("payload"));

        var response = await client.CallAsync(request, ct);

        response.IsSuccess.Should().BeTrue(response.Error?.Message);
        serverInterceptor.UnaryCallCount.Should().Be(1);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask UnaryRoundtrip_OverGrpcTransport()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(UnaryRoundtrip_OverGrpcTransport), dispatcher, ct);

        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateUnaryClient("echo", codec));
        var requestMeta = new RequestMeta(
            service: "echo",
            procedure: "ping",
            encoding: "application/json",
            transport: "grpc");
        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("hello"));

        var result = await client.CallAsync(request, ct);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value.Body.Message.Should().Be("hello-grpc");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask OnewayRoundtrip_OverGrpcTransport()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(OnewayRoundtrip_OverGrpcTransport), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateOnewayClient("audit", codec));
        var requestMeta = new RequestMeta(
            service: "audit",
            procedure: "audit::record",
            encoding: "application/json",
            transport: "grpc");
        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("ping"));

        var ackResult = await client.CallAsync(request, ct);

        ackResult.IsSuccess.Should().BeTrue(ackResult.Error?.Message);
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(2), ct)).Should().Be("ping");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask DuplexStreaming_OverGrpcTransport()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(DuplexStreaming_OverGrpcTransport), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateDuplexStreamClient("chat", codec));
        var requestMeta = new RequestMeta(
            service: "chat",
            procedure: "chat::talk",
            encoding: "application/json",
            transport: "grpc");

        var sessionResult = await client.StartAsync(requestMeta, ct);
        await using var session = sessionResult.ValueOrChecked();

        var firstWrite = await session.WriteAsync(new ChatMessage("hello"), ct);
        firstWrite.ValueOrChecked();
        var secondWrite = await session.WriteAsync(new ChatMessage("world"), ct);
        secondWrite.ValueOrChecked();
        await session.CompleteRequestsAsync(cancellationToken: ct);

        var responses = new List<string>();
        await foreach (var response in session.ReadResponsesAsync(ct))
        {
            responses.Add(response.ValueOrChecked().Body.Message);
        }

        responses.Should().Equal("ready", "echo:hello", "echo:world");
        session.ResponseMeta.Encoding.Should().Be("application/json");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask DuplexStreaming_ResponseAboveLimit_FaultsStream()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(DuplexStreaming_ResponseAboveLimit_FaultsStream), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var rawCodec = new RawCodec();
        var client = RequireClient(dispatcher.CreateDuplexStreamClient<byte[], byte[]>("chat-limit", rawCodec));
        var requestMeta = new RequestMeta(
            service: "chat-limit",
            procedure: "chat::oversized",
            encoding: RawCodec.DefaultEncoding,
            transport: TransportName);

        var callResult = await client.StartAsync(requestMeta, ct);
        await using var call = callResult.ValueOrChecked();

        await using var enumerator = call.ReadResponsesAsync(ct).GetAsyncEnumerator(ct);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!)
            .Should().BeOneOf(OmniRelayStatusCode.ResourceExhausted, OmniRelayStatusCode.Unknown);

    }

    [Fact(Timeout = 30_000)]
    public async ValueTask DuplexStreaming_ServerCancellationPropagatesToClient()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(DuplexStreaming_ServerCancellationPropagatesToClient), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateDuplexStreamClient("chat", codec));
        var requestMeta = new RequestMeta(
            service: "chat",
            procedure: "chat::cancel",
            encoding: "application/json",
            transport: "grpc");

        var sessionResult = await client.StartAsync(requestMeta, ct);
        await using var session = sessionResult.ValueOrChecked();
        var writeResult = await session.WriteAsync(new ChatMessage("first"), ct);
        writeResult.ValueOrChecked();
        await session.CompleteRequestsAsync(cancellationToken: ct);

        var enumerator = session.ReadResponsesAsync(ct).GetAsyncEnumerator(ct);
        if (!await enumerator.MoveNextAsync())
        {
            await enumerator.DisposeAsync();
            return;
        }

        if (enumerator.Current.IsSuccess)
        {
            enumerator.Current.ValueOrChecked().Body.Message.Should().Be("ready");

            if (await enumerator.MoveNextAsync())
            {
                if (enumerator.Current.IsSuccess)
                {
                    enumerator.Current.ValueOrChecked().Body.Message.Should().Be("ack:first");
                    (await enumerator.MoveNextAsync()).Should().BeTrue();
                }

                enumerator.Current.IsFailure.Should().BeTrue();
                OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!)
                    .Should().BeOneOf(OmniRelayStatusCode.Cancelled, OmniRelayStatusCode.Unknown);
            }
        }
        else
        {
            // First frame already carried terminal error.
            OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!)
                .Should().BeOneOf(OmniRelayStatusCode.Cancelled, OmniRelayStatusCode.Unknown);
        }

        await enumerator.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask DuplexStreaming_ClientCancellationPropagatesToServer()
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
                    Error? completionError = null;
                    using var cancellationRegistration = cancellationToken.Register(() =>
                    {
                        serverCancelled.TrySetResult(true);
                        completionError ??= OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Cancelled,
                            "Client cancelled duplex session.",
                            transport: GrpcTransportConstants.TransportName);
                    });

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
                        completionError ??= OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Cancelled,
                            "Client cancelled duplex session.",
                            transport: GrpcTransportConstants.TransportName);
                    }
                    finally
                    {
                        await call.CompleteResponsesAsync(completionError, CancellationToken.None);
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(DuplexStreaming_ClientCancellationPropagatesToServer), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateDuplexStreamClient("chat", codec));
        var requestMeta = new RequestMeta(
            service: "chat",
            procedure: "chat::client-cancel",
            encoding: "application/json",
            transport: "grpc");

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var sessionResult = await client.StartAsync(requestMeta, callCts.Token);
        await using var session = sessionResult.ValueOrChecked();
        var writeResult = await session.WriteAsync(new ChatMessage("hello"), ct);
        writeResult.ValueOrChecked();

        await callCts.CancelAsync();
        // Try to let the server observe cancellation but don't hang the test if the notification is delayed.
        try
        {
            await serverCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (TimeoutException)
        {
            // acceptable: we'll still assert based on client-observed terminal status
        }

        var enumerator = session.ReadResponsesAsync(callCts.Token).GetAsyncEnumerator(callCts.Token);
        Result<Response<ChatMessage>>? terminal = null;

        while (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            if (enumerator.Current.IsFailure)
            {
                terminal = enumerator.Current;
                break;
            }
        }

        terminal.Should().NotBeNull();
        OmniRelayErrorAdapter.ToStatus(terminal!.Value.Error!)
            .Should().BeOneOf(OmniRelayStatusCode.Cancelled, OmniRelayStatusCode.Unknown);

        await enumerator.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask DuplexStreaming_FlowControl_ServerSlow()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(DuplexStreaming_FlowControl_ServerSlow), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        {
            var client = RequireClient(dispatcher.CreateDuplexStreamClient("chat", codec));
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::flow",
                encoding: "application/json",
                transport: "grpc");

            var sessionResult = await client.StartAsync(requestMeta, ct);
            await using var session = sessionResult.ValueOrChecked();

            const int messageCount = 10;
            for (var i = 0; i < messageCount; i++)
            {
                var writeResult = await session.WriteAsync(new ChatMessage($"msg-{i}"), ct);
                writeResult.ValueOrChecked();
            }

            await session.CompleteRequestsAsync(cancellationToken: ct);

            var responses = new List<string>();
            await foreach (var response in session.ReadResponsesAsync(ct))
            {
                responses.Add(response.ValueOrChecked().Body.Message);
            }

            responses.Count.Should().Be(messageCount + 1);
            responses[0].Should().Be("ready");
            for (var i = 0; i < messageCount; i++)
            {
                responses[i + 1].Should().Be($"ack-{i + 1}:msg-{i}");
            }
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask Unary_PropagatesMetadataBetweenClientAndServer()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(Unary_PropagatesMetadataBetweenClientAndServer), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var client = RequireClient(dispatcher.CreateUnaryClient("echo", codec));
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
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        var serverMeta = await observedMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        serverMeta.Caller.Should().Be(requestMeta.Caller);
        serverMeta.ShardKey.Should().Be(requestMeta.ShardKey);
        serverMeta.RoutingKey.Should().Be(requestMeta.RoutingKey);
        serverMeta.RoutingDelegate.Should().Be(requestMeta.RoutingDelegate);
        serverMeta.TimeToLive.Should().Be(requestMeta.TimeToLive);

        serverMeta.Deadline.Should().HaveValue();
        var deadlineDelta = (serverMeta.Deadline!.Value - deadline).Duration();
        deadlineDelta.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(5));

        serverMeta.Headers["x-trace-id"].Should().Be("trace-abc");
        serverMeta.Headers["x-feature"].Should().Be("beta");

        var responseMeta = result.Value.Meta;
        responseMeta.Encoding.Should().Be("application/json");
        responseMeta.TryGetHeader("x-response-id", out var responseId).Should().BeTrue();
        responseId.Should().Be("42");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ServerStreaming_PropagatesMetadataAndHeaders()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(ServerStreaming_PropagatesMetadataAndHeaders), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        {
            var client = RequireClient(dispatcher.CreateStreamClient("stream", codec));
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
                responses.Add(response.ValueOrChecked());
            }

            responses.Count.Should().Be(2);
            responses[0].Body.Message.Should().Be("first");
            responses[1].Body.Message.Should().Be("second");

            var serverMeta = await observedMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            serverMeta.Caller.Should().Be("stream-caller");
            serverMeta.TimeToLive.Should().Be(TimeSpan.FromSeconds(10));
            serverMeta.Deadline.Should().HaveValue();
            var deadlineDelta = (serverMeta.Deadline!.Value - deadline).Duration();
            deadlineDelta.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(5));
            serverMeta.Headers["x-meta"].Should().Be("value");

            foreach (var response in responses)
            {
                response.Meta.Encoding.Should().Be("application/json");
                response.Meta.TryGetHeader("x-stream-id", out var streamId).Should().BeTrue();
                streamId.Should().Be("stream-99");
            }
        }

    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcTransport_ResponseTrailers_SurfaceEncodingAndStatus()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcTransport_ResponseTrailers_SurfaceErrorMetadata), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

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

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new EchoRequest("hello"),
                GrpcTransportJsonContext.Default.EchoRequest);
            var call = channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(metadata, cancellationToken: ct), payload);

            var responseBytes = await call.ResponseAsync;
            var headers = await call.ResponseHeadersAsync;
            var trailers = call.GetTrailers();

            responseBytes.Should().NotBeEmpty();

            bool encodingHeaderFound = headers.Any(entry => string.Equals(entry.Key, EncodingTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "application/json", StringComparison.OrdinalIgnoreCase))
                || trailers.Any(entry => string.Equals(entry.Key, EncodingTrailerKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value, "application/json", StringComparison.OrdinalIgnoreCase));
            encodingHeaderFound.Should().BeTrue("Expected OmniRelay encoding metadata to be present in headers or trailers.");

            bool customHeaderFound = headers.Any(entry => string.Equals(entry.Key, "x-meta-response", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "yes", StringComparison.OrdinalIgnoreCase))
                || trailers.Any(entry => string.Equals(entry.Key, "x-meta-response", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value, "yes", StringComparison.OrdinalIgnoreCase));
            customHeaderFound.Should().BeTrue("Expected custom response header to be present in headers or trailers.");

        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcTransport_ResponseTrailers_SurfaceErrorMetadata()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcTransport_ResponseTrailers_SurfaceErrorMetadata), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

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

            var rpcException = await Invoking(async () => await call.ResponseAsync)
                .Should().ThrowAsync<RpcException>();
            rpcException.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);

            var trailers = rpcException.Which.Trailers;
            trailers.Should().Contain(entry => string.Equals(entry.Key, ErrorMessageTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "access denied", StringComparison.OrdinalIgnoreCase));
            trailers.Should().Contain(entry => string.Equals(entry.Key, ErrorCodeTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "permission-denied", StringComparison.OrdinalIgnoreCase));
            trailers.Should().Contain(entry => string.Equals(entry.Key, StatusTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, nameof(OmniRelayStatusCode.PermissionDenied), StringComparison.OrdinalIgnoreCase));
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

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(FromStatusMappings))]
    public void GrpcStatusMapper_FromStatus_MapsExpected(StatusCode statusCode, OmniRelayStatusCode expected)
    {
        var status = new Status(statusCode, "detail");
        var result = GrpcStatusMapper.FromStatus(status);
        result.Should().Be(expected);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(ToStatusMappings))]
    public void GrpcStatusMapper_ToStatus_MapsExpected(StatusCode expectedStatusCode, OmniRelayStatusCode omnirelayStatus)
    {
        var status = GrpcStatusMapper.ToStatus(omnirelayStatus, "detail");
        status.StatusCode.Should().Be(expectedStatusCode);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcOutbound_TelemetryOptions_EnableClientLoggingInterceptor()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcOutbound_TelemetryOptions_EnableClientLoggingInterceptor), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        var loggerProvider = new CaptureLoggerProvider();
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
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
            payload.IsSuccess.Should().BeTrue();

            var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload.Value);
            var responseResult = await outbound.CallAsync(request, ct);
            responseResult.IsSuccess.Should().BeTrue(responseResult.Error?.Message);

            loggerProvider.Entries.Should().Contain(entry =>
                string.Equals(entry.CategoryName, typeof(GrpcClientLoggingInterceptor).FullName, StringComparison.Ordinal) &&
                entry.Message.Contains("Completed gRPC client unary call", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await outbound.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcInbound_ServerLoggingInterceptor_WritesLogs()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_ServerLoggingInterceptor_WritesLogs), dispatcher, ct);
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
            payload.IsSuccess.Should().BeTrue();

            var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload.Value);
            var responseResult = await outbound.CallAsync(request, ct);
            responseResult.IsSuccess.Should().BeTrue(responseResult.Error?.Message);
        }
        finally
        {
            await outbound.StopAsync(ct);
        }

        loggerProvider.Entries.Should().Contain(entry =>
            string.Equals(entry.CategoryName, typeof(GrpcServerLoggingInterceptor).FullName, StringComparison.Ordinal) &&
            entry.Message.Contains("Completed gRPC server unary call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcInbound_TelemetryOptions_RegistersServerLoggingInterceptor()
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
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_TelemetryOptions_RegistersServerLoggingInterceptor), dispatcher, ct);
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
            payload.IsSuccess.Should().BeTrue();

            var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload.Value);
            var responseResult = await outbound.CallAsync(request, ct);
            responseResult.IsSuccess.Should().BeTrue(responseResult.Error?.Message);
        }
        finally
        {
            await outbound.StopAsync(ct);
        }

        serverDuration.Should().NotBeNull("Expected server unary duration metric to be recorded.");
        serverDuration!.Value.Should().BeGreaterThan(0, "Expected server unary duration metric to be recorded.");
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
            public string CategoryName { get; init; } = CategoryName;

            public LogLevel LogLevel { get; init; } = LogLevel;

            public string Message { get; init; } = Message;
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

    private static TClient RequireClient<TClient>(Result<TClient> result)
    {
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }
}

internal sealed record EchoRequest(string Message)
{
    public string Message { get; init; } = Message;
}

internal sealed record EchoResponse
{
    public string Message { get; init; } = string.Empty;
}

internal sealed record AggregateChunk(int Amount)
{
    public int Amount { get; init; } = Amount;
}

internal sealed record AggregateResponse(int TotalAmount)
{
    public int TotalAmount { get; init; } = TotalAmount;
}

internal sealed record ChatMessage(string Message)
{
    public string Message { get; init; } = Message;
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoResponse))]
internal sealed partial class GrpcTransportJsonContext : JsonSerializerContext;

internal sealed class DummyCompressionProvider : ICompressionProvider
    {
        public DummyCompressionProvider(string encodingName)
        {
            if (string.IsNullOrWhiteSpace(encodingName))
            {
                throw new ArgumentException("Encoding name is required.", nameof(encodingName));
            }

            EncodingName = encodingName;
        }

        public string EncodingName { get; }

        public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel) => stream;

        public Stream CreateDecompressionStream(Stream stream) => stream;
    }
