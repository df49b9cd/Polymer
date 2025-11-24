using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.Compression;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Grpc.Interceptors;
using Xunit;
using static AwesomeAssertions.FluentActions;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests.Transport.Grpc;

public class GrpcHttp3NegotiationTests(ITestOutputHelper output) : TransportIntegrationTest(output)
{
    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcInbound_WithHttp3Enabled_ExecutesInterceptorsOverHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http3");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new ConcurrentQueue<string>();
        var requestMetaProtocols = new ConcurrentQueue<string>();
        var activities = new ConcurrentQueue<Activity>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, GrpcTransportDiagnostics.ActivitySourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Enqueue(activity)
        };

        ActivitySource.AddActivityListener(activityListener);
        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = [typeof(AuthorizationInterceptor), typeof(GrpcServerLoggingInterceptor)]
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("grpc-http3");
        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(observedProtocols);
                services.AddSingleton<AuthorizationInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: runtime);
        options.AddLifecycle("grpc-http3-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-http3",
            "grpc-http3::ping",
            (request, _) =>
            {
                if (request.Meta.Headers.TryGetValue("rpc.protocol", out var protocol) && !string.IsNullOrWhiteSpace(protocol))
                {
                    requestMetaProtocols.Enqueue(protocol);
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3Enabled_ExecutesInterceptorsOverHttp3), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http3", "grpc-http3::ping", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var metadata = new Metadata { { "authorization", "Bearer test-token" } };

        try
        {
            var call = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
            var response = await call.ResponseAsync.WaitAsync(ct);
            response.Should().BeEmpty();
        }
        finally
        {
        }

        observedProtocols.TryDequeue(out var protocol).Should().BeTrue("No HTTP protocol was observed by the interceptor.");
        protocol.Should().StartWithEquivalentOf("HTTP/3");

        requestMetaProtocols.TryDequeue(out var metaProtocol).Should().BeTrue("No HTTP protocol was captured in request metadata.");
        metaProtocol.Should().StartWithEquivalentOf("HTTP/3");

        var recordedActivity = activities.LastOrDefault(activity => string.Equals(activity.OperationName, "grpc.server.unary", StringComparison.Ordinal));
        recordedActivity.Should().NotBeNull();
        recordedActivity!.GetTagItem("network.protocol.name").Should().Be("http");
        recordedActivity.GetTagItem("network.protocol.version")?.ToString().Should().StartWith("3");
        recordedActivity.GetTagItem("rpc.protocol")?.ToString().Should().StartWithEquivalentOf("HTTP/3");
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcInbound_WithHttp3Enabled_RunsTransportInterceptorsOverHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http3-transport");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var runtimeProtocols = new ConcurrentQueue<string>();
        var transportProtocols = new ConcurrentQueue<string>();
        var requestMetaProtocols = new ConcurrentQueue<string>();

        var runtimeOptions = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = [typeof(AuthorizationInterceptor), typeof(GrpcServerLoggingInterceptor)]
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };
        var options = new DispatcherOptions("grpc-http3-transport");

        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(runtimeProtocols);
                services.AddSingleton<AuthorizationInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: runtimeOptions);

        var interceptorBuilder = new GrpcTransportInterceptorBuilder();
        interceptorBuilder.UseServer(new TransportProtocolInterceptor(transportProtocols));
        var transportRegistry = interceptorBuilder.BuildServerRegistry();
        ((IGrpcServerInterceptorSink)inbound).AttachGrpcServerInterceptors(transportRegistry!);

        options.AddLifecycle("grpc-http3-transport-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-http3-transport",
            "grpc-http3-transport::ping",
            (request, _) =>
            {
                if (request.Meta.Headers.TryGetValue("rpc.protocol", out var protocol) && !string.IsNullOrWhiteSpace(protocol))
                {
                    requestMetaProtocols.Enqueue(protocol);
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3Enabled_RunsTransportInterceptorsOverHttp3), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http3-transport", "grpc-http3-transport::ping", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var metadata = new Metadata { { "authorization", "Bearer test-token" } };

        var call = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
        var response = await call.ResponseAsync.WaitAsync(ct);
        response.Should().BeEmpty();

        runtimeProtocols.TryDequeue(out var runtimeProtocol).Should().BeTrue("No HTTP protocol was observed by the runtime interceptor.");
        runtimeProtocol.Should().StartWithEquivalentOf("HTTP/3");

        transportProtocols.TryDequeue(out var transportProtocol).Should().BeTrue("No HTTP protocol was observed by the transport interceptor.");
        transportProtocol.Should().StartWithEquivalentOf("HTTP/3");

        requestMetaProtocols.TryDequeue(out var metaProtocol).Should().BeTrue("No HTTP protocol was captured in request metadata.");
        metaProtocol.Should().StartWithEquivalentOf("HTTP/3");
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcInbound_WithHttp3Disabled_FallsBackToHttp2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http2");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new ConcurrentQueue<string>();
        var transportProtocols = new ConcurrentQueue<string>();
        var requestMetaProtocols = new ConcurrentQueue<string>();
        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = false,
            Interceptors = [typeof(AuthorizationInterceptor)]
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };
        var options = new DispatcherOptions("grpc-http2");

        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(observedProtocols);
                services.AddSingleton<AuthorizationInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: runtime);

        var interceptorBuilder = new GrpcTransportInterceptorBuilder();
        interceptorBuilder.UseServer(new TransportProtocolInterceptor(transportProtocols));
        var transportRegistry = interceptorBuilder.BuildServerRegistry();
        ((IGrpcServerInterceptorSink)inbound).AttachGrpcServerInterceptors(transportRegistry!);

        options.AddLifecycle("grpc-http2-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-http2",
            "grpc-http2::ping",
            (request, _) =>
            {
                if (request.Meta.Headers.TryGetValue("rpc.protocol", out var protocol) && !string.IsNullOrWhiteSpace(protocol))
                {
                    requestMetaProtocols.Enqueue(protocol);
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3Disabled_FallsBackToHttp2), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionOrLower);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http2", "grpc-http2::ping", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var metadata = new Metadata { { "authorization", "Bearer test-token" } };

        try
        {
        var call = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
        var response = await call.ResponseAsync.WaitAsync(ct);
        response.Should().BeEmpty();
        }
        finally
        {
        }

        observedProtocols.TryDequeue(out var protocol).Should().BeTrue("No HTTP protocol was observed by the interceptor.");
        protocol.Should().StartWithEquivalentOf("HTTP/2");

        transportProtocols.TryDequeue(out var transportProtocol).Should().BeTrue("No HTTP protocol was observed by the transport interceptor.");
        transportProtocol.Should().StartWithEquivalentOf("HTTP/2");

        requestMetaProtocols.TryDequeue(out var metaProtocol).Should().BeTrue("No HTTP protocol was captured in request metadata.");
        metaProtocol.Should().StartWithEquivalentOf("HTTP/2");
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcInbound_WithHttp3_ServerStreamHandlesLargePayload()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http3-stream");

        var payload = new byte[512 * 1024];
        RandomNumberGenerator.Fill(payload);

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var acceptEncodings = new ConcurrentQueue<string>();

        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = [typeof(AcceptEncodingCaptureInterceptor)]
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };
        var options = new DispatcherOptions("grpc-http3-stream");

        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(acceptEncodings);
                services.AddSingleton<AcceptEncodingCaptureInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: runtime);
        options.AddLifecycle("grpc-http3-stream-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new StreamProcedureSpec(
            "grpc-http3-stream",
            "grpc-http3-stream::tail",
            async (request, streamOptions, cancellationToken) =>
            {
                var call = GrpcServerStreamCall.Create(request.Meta, new ResponseMeta());
                await call.WriteAsync(payload, cancellationToken);
                await call.CompleteAsync(cancellationToken: cancellationToken);
                return Ok<IStreamCall>(call);
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3_ServerStreamHandlesLargePayload), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var invoker = channel.CreateCallInvoker();

        var method = new Method<byte[], byte[]>(MethodType.ServerStreaming, "grpc-http3-stream", "grpc-http3-stream::tail", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        try
        {
            var streamingCall = invoker.AsyncServerStreamingCall(method, null, new CallOptions(), []);
            (await streamingCall.ResponseStream.MoveNext(ct)).Should().BeTrue();
            var message = streamingCall.ResponseStream.Current;
            message.Length.Should().Be(payload.Length);
            message.SequenceEqual(payload).Should().BeTrue();
            (await streamingCall.ResponseStream.MoveNext(ct)).Should().BeFalse();
        }
        finally
        {
        }

        acceptEncodings.TryDequeue(out var negotiatedEncoding).Should().BeTrue("No grpc-accept-encoding header was observed.");
        negotiatedEncoding.Should().NotBeNullOrWhiteSpace();
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcInbound_WithHttp3_DuplexHandlesLargePayloads()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http3-duplex");

        var payload = new byte[256 * 1024];
        RandomNumberGenerator.Fill(payload);

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };
        var options = new DispatcherOptions("grpc-http3-duplex");

        var inbound = new GrpcInbound(
            [address.ToString()],
            serverTlsOptions: tls,
            serverRuntimeOptions: runtime);
        options.AddLifecycle("grpc-http3-duplex-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new DuplexProcedureSpec(
            "grpc-http3-duplex",
            "grpc-http3-duplex::echo",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta());
                _ = Task.Run(async () =>
                {
                    await foreach (var frame in call.RequestReader.ReadAllAsync(cancellationToken))
                    {
                        await call.ResponseWriter.WriteAsync(frame, cancellationToken);
                    }

                    await call.CompleteResponsesAsync(cancellationToken: cancellationToken);
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IDuplexStreamCall>(call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3_DuplexHandlesLargePayloads), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var invoker = channel.CreateCallInvoker();

        var method = new Method<byte[], byte[]>(MethodType.DuplexStreaming, "grpc-http3-duplex", "grpc-http3-duplex::echo", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        try
        {
            var call = invoker.AsyncDuplexStreamingCall(method, null, new CallOptions());
            await call.RequestStream.WriteAsync(payload, cancellationToken: ct);
            await call.RequestStream.CompleteAsync().WaitAsync(ct);

            (await call.ResponseStream.MoveNext(ct)).Should().BeTrue();
            var echo = call.ResponseStream.Current;
            echo.Length.Should().Be(payload.Length);
            echo.SequenceEqual(payload).Should().BeTrue();
            (await call.ResponseStream.MoveNext(ct)).Should().BeFalse();
        }
        finally
        {
        }
    }

    [Http3Fact(Timeout = 60_000)]
    public async ValueTask GrpcInbound_WithHttp3_KeepAliveMaintainsDuplex()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http3-keepalive");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Http3 = new OmniRelay.Transport.Http.Http3RuntimeOptions
            {
                KeepAliveInterval = TimeSpan.FromMilliseconds(500)
            }
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };
        var options = new DispatcherOptions("grpc-http3-keepalive");

        var inbound = new GrpcInbound(
            [address.ToString()],
            serverTlsOptions: tls,
            serverRuntimeOptions: runtime);
        options.AddLifecycle("grpc-http3-keepalive-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new DuplexProcedureSpec(
            "grpc-http3-keepalive",
            "grpc-http3-keepalive::echo",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta());
                _ = Task.Run(async () =>
                {
                    await foreach (var frame in call.RequestReader.ReadAllAsync(cancellationToken))
                    {
                        await call.ResponseWriter.WriteAsync(frame, cancellationToken);
                    }

                    await call.CompleteResponsesAsync(cancellationToken: cancellationToken);
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IDuplexStreamCall>(call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3_KeepAliveMaintainsDuplex), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.DuplexStreaming, "grpc-http3-keepalive", "grpc-http3-keepalive::echo", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        var payload1 = new byte[16 * 1024];
        RandomNumberGenerator.Fill(payload1);
        var payload2 = new byte[8 * 1024];
        RandomNumberGenerator.Fill(payload2);

        try
        {
            var call = invoker.AsyncDuplexStreamingCall(method, null, new CallOptions());
            await call.RequestStream.WriteAsync(payload1, cancellationToken: ct);

            // Wait beyond the server keep-alive interval to ensure the connection is kept alive
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            await call.RequestStream.WriteAsync(payload2, cancellationToken: ct);
            await call.RequestStream.CompleteAsync().WaitAsync(ct);

            (await call.ResponseStream.MoveNext(ct)).Should().BeTrue();
            call.ResponseStream.Current.Length.Should().Be(payload1.Length);

            (await call.ResponseStream.MoveNext(ct)).Should().BeTrue();
            call.ResponseStream.Current.Length.Should().Be(payload2.Length);

            (await call.ResponseStream.MoveNext(ct)).Should().BeFalse();
        }
        finally
        {
        }
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcInbound_WithHttp3Enabled_DrainRejectsNewCallsWithRetryAfter()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http3-drain");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new ConcurrentQueue<string>();
        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = [typeof(AuthorizationInterceptor)]
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("grpc-http3-drain");
        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(observedProtocols);
                services.AddSingleton<AuthorizationInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: runtime);
        options.AddLifecycle("grpc-http3-drain-inbound", inbound);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-http3-drain",
            "grpc-http3-drain::slow",
            async (_, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3Enabled_DrainRejectsNewCallsWithRetryAfter), dispatcher, ct, ownsLifetime: false);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http3-drain", "grpc-http3-drain::slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var metadata = new Metadata { { "authorization", "Bearer test-token" } };

        Task? stopTask = null;
        try
        {
            var inFlightCall = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
            await requestStarted.Task.WaitAsync(ct);

            stopTask = dispatcher.StopAsyncChecked(ct);
            await Task.Delay(100, ct);

            var rejectedCall = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
            var rejection = await Invoking(() => rejectedCall.ResponseAsync)
                .Should().ThrowAsync<RpcException>();
            rejection.Which.StatusCode.Should().Be(StatusCode.Unavailable);
            rejection.Which.Trailers.GetValue("retry-after").Should().Be("1");

            releaseRequest.TrySetResult();
            var response = await inFlightCall.ResponseAsync.WaitAsync(ct);
            response.Should().BeEmpty();

            await stopTask;
        }
        finally
        {
            if (stopTask is not null)
            {
                await stopTask;
            }
            else
            {
                await dispatcher.StopAsyncChecked(ct);
            }
        }

        observedProtocols.TryDequeue(out var protocol).Should().BeTrue("No HTTP protocol was observed by the interceptor.");
        protocol.Should().StartWithEquivalentOf("HTTP/3");
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcInbound_WithHttp3Enabled_CompressionNegotiatesGzip()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http3-compression");

        var payload = new byte[128 * 1024];
        RandomNumberGenerator.Fill(payload);

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var acceptEncodings = new ConcurrentQueue<string>();

        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = [typeof(AcceptEncodingCaptureInterceptor)]
        };

        var compressionOptions = new GrpcCompressionOptions
        {
            Providers = [new GzipCompressionProvider(CompressionLevel.Optimal)],
            DefaultAlgorithm = "gzip",
            DefaultCompressionLevel = CompressionLevel.Optimal
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };
        var options = new DispatcherOptions("grpc-http3-compression");

        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(acceptEncodings);
                services.AddSingleton<AcceptEncodingCaptureInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: runtime,
            compressionOptions: compressionOptions);
        options.AddLifecycle("grpc-http3-compression-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-http3-compression",
            "grpc-http3-compression::compressed",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3Enabled_CompressionNegotiatesGzip), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            CompressionProviders = [new GzipCompressionProvider(CompressionLevel.Optimal)]
        });

        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http3-compression", "grpc-http3-compression::compressed", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        try
        {
            using var call = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
            var response = await call.ResponseAsync.WaitAsync(ct);
            response.Length.Should().Be(payload.Length);

            var headers = await call.ResponseHeadersAsync.WaitAsync(ct);
            var encoding = headers.GetValue("grpc-encoding") ?? call.GetTrailers().GetValue("grpc-encoding");
            if (encoding is not null)
            {
                encoding.Should().Be("gzip");
            }
        }
        finally
        {
        }

        acceptEncodings.TryDequeue(out var negotiated).Should().BeTrue("No grpc-accept-encoding header was observed.");
        negotiated.Should().NotBeNullOrWhiteSpace();
        negotiated
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain(value => value.Equals("gzip", StringComparison.OrdinalIgnoreCase));
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcInbound_WithHttp3Disabled_CompressionNegotiatesGzip()
    {
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-http2-compression");

        var payload = new byte[128 * 1024];
        RandomNumberGenerator.Fill(payload);

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var acceptEncodings = new ConcurrentQueue<string>();

        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = false,
            Interceptors = [typeof(AcceptEncodingCaptureInterceptor)]
        };

        var compressionOptions = new GrpcCompressionOptions
        {
            Providers = [new GzipCompressionProvider(CompressionLevel.Optimal)],
            DefaultAlgorithm = "gzip",
            DefaultCompressionLevel = CompressionLevel.Optimal
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };
        var options = new DispatcherOptions("grpc-http2-compression");

        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(acceptEncodings);
                services.AddSingleton<AcceptEncodingCaptureInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: runtime,
            compressionOptions: compressionOptions);
        options.AddLifecycle("grpc-http2-compression-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-http2-compression",
            "grpc-http2-compression::compressed",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(GrpcInbound_WithHttp3Disabled_CompressionNegotiatesGzip), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3Handler(HttpVersionPolicy.RequestVersionOrLower);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            CompressionProviders = [new GzipCompressionProvider(CompressionLevel.Optimal)]
        });

        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http2-compression", "grpc-http2-compression::compressed", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        try
        {
            using var call = invoker.AsyncUnaryCall(method, null, new CallOptions(), []);
            var response = await call.ResponseAsync.WaitAsync(ct);
            response.Length.Should().Be(payload.Length);

            var headers = await call.ResponseHeadersAsync.WaitAsync(ct);
            var encoding = headers.GetValue("grpc-encoding") ?? call.GetTrailers().GetValue("grpc-encoding");
            if (encoding is not null)
            {
                encoding.Should().Be("gzip");
            }
        }
        finally
        {
        }

        acceptEncodings.TryDequeue(out var negotiated).Should().BeTrue("No grpc-accept-encoding header was observed.");
        negotiated.Should().NotBeNullOrWhiteSpace();
        negotiated
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain(value => value.Equals("gzip", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
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
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port)
                            .WaitAsync(TimeSpan.FromMilliseconds(connectTimeoutMilliseconds), cancellationToken);

                await Task.Delay(TimeSpan.FromMilliseconds(settleDelayMilliseconds), cancellationToken);
                return;
            }
            catch (SocketException)
            {
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
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

    private static HttpMessageHandler CreateHttp3Handler(HttpVersionPolicy policy) =>
        new Http3VersionHandler(CreateHttp3SocketsHandler(), policy);

    private sealed class Http3VersionHandler(HttpMessageHandler inner, HttpVersionPolicy policy)
        : DelegatingHandler(inner)
    {
        private readonly HttpVersionPolicy _policy = policy;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version30;
            request.VersionPolicy = _policy;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class AuthorizationInterceptor(ConcurrentQueue<string> observedProtocols) : Interceptor
    {
        private readonly ConcurrentQueue<string> _observedProtocols = observedProtocols ?? throw new ArgumentNullException(nameof(observedProtocols));

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(continuation);

            var httpContext = context.GetHttpContext();
            if (httpContext is not null)
            {
                _observedProtocols.Enqueue(httpContext.Request.Protocol);
            }

            var authorization = context.RequestHeaders.FirstOrDefault(static entry =>
                string.Equals(entry.Key, "authorization", StringComparison.OrdinalIgnoreCase));

            if (authorization is null ||
                !string.Equals(authorization.Value, "Bearer test-token", StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "missing authorization header"));
            }

            return await continuation(request, context);
        }
    }

    private sealed class TransportProtocolInterceptor(ConcurrentQueue<string> observedProtocols) : Interceptor
    {
        private readonly ConcurrentQueue<string> _observedProtocols = observedProtocols ?? throw new ArgumentNullException(nameof(observedProtocols));

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(continuation);

            var httpContext = context.GetHttpContext();
            if (httpContext is not null)
            {
                _observedProtocols.Enqueue(httpContext.Request.Protocol);
            }

            return await continuation(request, context);
        }
    }

    private sealed class AcceptEncodingCaptureInterceptor(ConcurrentQueue<string> encodings) : Interceptor
    {
        private readonly ConcurrentQueue<string> _encodings = encodings ?? throw new ArgumentNullException(nameof(encodings));

        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(continuation);

            CaptureEncoding(context);
            return continuation(request, context);
        }

        public override Task ServerStreamingServerHandler<TRequest, TResponse>(
            TRequest request,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            ArgumentNullException.ThrowIfNull(responseStream);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(continuation);

            CaptureEncoding(context);
            return continuation(request, responseStream, context);
        }

        private void CaptureEncoding(ServerCallContext context)
        {
            var httpContext = context.GetHttpContext();
            if (httpContext is not null &&
                httpContext.Request.Headers.TryGetValue("grpc-accept-encoding", out var values))
            {
                var value = values.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _encodings.Enqueue(value);
                }
            }
        }
    }
}
