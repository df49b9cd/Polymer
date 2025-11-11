using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests;
using OmniRelay.Tests.Protos;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Grpc.Interceptors;
using OmniRelay.Transport.Http;
using Xunit;
using Xunit.Sdk;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class GrpcTransportIntegrationTests
{
    private const string ServiceName = "grpc-integration";
    private const string GrpcTransportName = "grpc";
    private static readonly TimeSpan DeadlineTolerance = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 60_000)]
    public async Task Http2_CoversAllRpcShapes_WithMetadataAndInterceptors()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var serverLog = new ConcurrentQueue<string>();
        var clientLog = new ConcurrentQueue<string>();
        var protocolLog = new ConcurrentQueue<string>();

        var serviceImpl = new GeneratedTestService();

        var serverOptions = new DispatcherOptions(ServiceName);
        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(protocolLog);
                services.AddSingleton<ProtocolCaptureInterceptor>();
            },
            serverRuntimeOptions: new GrpcServerRuntimeOptions
            {
                Interceptors = [typeof(ProtocolCaptureInterceptor)]
            });
        serverOptions.AddLifecycle("grpc-h2-inbound", inbound);
        serverOptions.GrpcInterceptors.UseServer(new RecordingServerInterceptor("server-global", serverLog));
        serverOptions.GrpcInterceptors.ForServerProcedure("UnaryCall").Use(new RecordingServerInterceptor("server-unary", serverLog));
        var serverDispatcher = new Dispatcher.Dispatcher(serverOptions);
        serverDispatcher.RegisterTestService(serviceImpl);

        var outbound = new GrpcOutbound(address, ServiceName, clientRuntimeOptions: new GrpcClientRuntimeOptions
        {
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
        });

        var clientOptions = new DispatcherOptions("grpc-client-h2");
        clientOptions.AddUnaryOutbound(ServiceName, null, outbound);
        clientOptions.AddStreamOutbound(ServiceName, null, outbound);
        clientOptions.AddClientStreamOutbound(ServiceName, null, outbound);
        clientOptions.AddDuplexOutbound(ServiceName, null, outbound);
        clientOptions.GrpcInterceptors.UseClient(new RecordingClientInterceptor("client-global", clientLog));
        var clientDispatcher = new Dispatcher.Dispatcher(clientOptions);

        var ct = TestContext.Current.CancellationToken;
        await serverDispatcher.StartOrThrowAsync(ct);
        await clientDispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = TestServiceOmniRelay.CreateTestServiceClient(clientDispatcher, ServiceName);
            await ExerciseGeneratedClientAsync(client, serviceImpl, ct);
        }
        finally
        {
            await clientDispatcher.StopOrThrowAsync(CancellationToken.None);
            await serverDispatcher.StopOrThrowAsync(CancellationToken.None);
        }

        AssertLogContains(clientLog, "client-global");
        AssertLogContains(serverLog, "server-global");
        AssertLogContains(protocolLog, "HTTP/2");
    }

    [Http3Fact(Timeout = 90_000)]
    public async Task Http3_CoversAllRpcShapes_WithGeneratedService()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=grpc-http3");
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var serverLog = new ConcurrentQueue<string>();
        var clientLog = new ConcurrentQueue<string>();
        var protocolLog = new ConcurrentQueue<string>();
        var serviceImpl = new GeneratedTestService();

        var serverRuntime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Http3 = new Http3RuntimeOptions
            {
                IdleTimeout = TimeSpan.FromSeconds(30),
                KeepAliveInterval = TimeSpan.FromSeconds(5),
                MaxBidirectionalStreams = 10,
                MaxUnidirectionalStreams = 5,
                EnableAltSvc = true
            },
            Interceptors = [typeof(ProtocolCaptureInterceptor)]
        };

        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(protocolLog);
                services.AddSingleton<ProtocolCaptureInterceptor>();
            },
            serverTlsOptions: new GrpcServerTlsOptions { Certificate = certificate },
            serverRuntimeOptions: serverRuntime);

        var serverOptions = new DispatcherOptions(ServiceName);
        serverOptions.AddLifecycle("grpc-http3-inbound", inbound);
        serverOptions.GrpcInterceptors.UseServer(new RecordingServerInterceptor("server-http3", serverLog));
        var serverDispatcher = new Dispatcher.Dispatcher(serverOptions);
        serverDispatcher.RegisterTestService(serviceImpl);

        var outbound = new GrpcOutbound(
            address,
            ServiceName,
            clientTlsOptions: new GrpcClientTlsOptions
            {
                EnabledProtocols = SslProtocols.Tls13,
                CheckCertificateRevocation = false,
                ServerCertificateValidationCallback = static (_, _, _, _) => true
            },
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                RequestVersion = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                AllowHttp2Fallback = false
            });

        var clientOptions = new DispatcherOptions("grpc-client-http3");
        clientOptions.AddUnaryOutbound(ServiceName, null, outbound);
        clientOptions.AddStreamOutbound(ServiceName, null, outbound);
        clientOptions.AddClientStreamOutbound(ServiceName, null, outbound);
        clientOptions.AddDuplexOutbound(ServiceName, null, outbound);
        clientOptions.GrpcInterceptors.UseClient(new RecordingClientInterceptor("client-http3", clientLog));
        var clientDispatcher = new Dispatcher.Dispatcher(clientOptions);

        var ct = TestContext.Current.CancellationToken;
        await serverDispatcher.StartOrThrowAsync(ct);
        await clientDispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = TestServiceOmniRelay.CreateTestServiceClient(clientDispatcher, ServiceName);
            var warmup = await client.UnaryCallAsync(new UnaryRequest { Message = "h3-warmup" }, cancellationToken: ct);
            Assert.True(warmup.IsSuccess, warmup.Error?.Message);
            await Task.Delay(250, ct);
            serviceImpl.ResetProbes();
            await ExerciseGeneratedClientAsync(client, serviceImpl, ct);
        }
        finally
        {
            await clientDispatcher.StopOrThrowAsync(CancellationToken.None);
            await serverDispatcher.StopOrThrowAsync(CancellationToken.None);
        }

        // Guardrail: Grpc.Net.Client currently downgrades to HTTP/2 even with RequestVersionExact.
        // When the client stack starts honoring HTTP/3, this test will fail and should be updated
        // to expect HTTP/3 again.
        var negotiatedProtocols = protocolLog.ToArray();
        if (negotiatedProtocols.Any(entry => entry.Contains("HTTP/3", StringComparison.OrdinalIgnoreCase)))
        {
            throw new XunitException("Grpc.Net.Client negotiated HTTP/3; update the guardrail expectations.");
        }

        AssertLogContains(clientLog, "client-http3");
        AssertLogContains(serverLog, "server-http3");
        AssertLogContains(protocolLog, "HTTP/2");
    }

    [Fact(Timeout = 60_000)]
    public async Task TlsMutualAuthAndAlpnPoliciesAreEnforced()
    {
        using var serverCert = TestCertificateFactory.CreateLoopbackCertificate("CN=grpc-mtls-server");
        using var clientCert = TestCertificateFactory.CreateLoopbackCertificate("CN=grpc-mtls-client");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var tlsOptions = new GrpcServerTlsOptions
        {
            Certificate = serverCert,
            ClientCertificateMode = ClientCertificateMode.RequireCertificate,
            ClientCertificateValidation = (cert, _, _) =>
                cert is not null && string.Equals(cert.Thumbprint, clientCert.Thumbprint, StringComparison.OrdinalIgnoreCase)
        };

        var options = new DispatcherOptions(ServiceName);
        var inbound = new GrpcInbound([address.ToString()], serverTlsOptions: tlsOptions);
        options.AddLifecycle("grpc-mtls-inbound", inbound);

        var dispatcher = new Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            ServiceName,
            "tls::ping",
            (request, _) =>
            {
                var meta = new ResponseMeta(encoding: MediaTypeNames.Text.Plain);
                var payload = "pong"u8.ToArray();
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, meta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var insecureHandler = new SocketsHttpHandler();
        using var secureHandler = new SocketsHttpHandler();
        using var alpnHandler = new SocketsHttpHandler();

        insecureHandler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        secureHandler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        alpnHandler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        secureHandler.SslOptions.ClientCertificates = new X509Certificate2Collection(clientCert);
        alpnHandler.SslOptions.ClientCertificates = new X509Certificate2Collection(clientCert);

        try
        {
            var insecureOutbound = new GrpcOutbound(
                address,
                ServiceName,
                channelOptions: new GrpcChannelOptions { HttpHandler = insecureHandler });

            var meta = new RequestMeta(service: ServiceName, procedure: "tls::ping", encoding: MediaTypeNames.Text.Plain);
            var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
            await insecureOutbound.StartAsync(ct);
            var insecureResult = await ((IUnaryOutbound)insecureOutbound).CallAsync(request, ct);
            Assert.True(insecureResult.IsFailure);
            await insecureOutbound.StopAsync(CancellationToken.None);

            var secureOutbound = new GrpcOutbound(
                address,
                ServiceName,
                channelOptions: new GrpcChannelOptions { HttpHandler = secureHandler });

            await secureOutbound.StartAsync(ct);
            var secureResult = await ((IUnaryOutbound)secureOutbound).CallAsync(request, ct);
            Assert.True(secureResult.IsSuccess);
            await secureOutbound.StopAsync(CancellationToken.None);

            var alpnOutbound = new GrpcOutbound(
                address,
                ServiceName,
                channelOptions: new GrpcChannelOptions { HttpHandler = alpnHandler },
                clientRuntimeOptions: new GrpcClientRuntimeOptions
                {
                    EnableHttp3 = true,
                    RequestVersion = HttpVersion.Version30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                });

            await alpnOutbound.StartAsync(ct);
            var alpnResult = await ((IUnaryOutbound)alpnOutbound).CallAsync(request, ct);
            Assert.True(alpnResult.IsFailure);
            await alpnOutbound.StopAsync(CancellationToken.None);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Http3Fact(Timeout = 45_000)]
    public async Task Http3Runtime_Tuning_EmitsInformationLog()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=grpc-http3-logs");
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");
        var observed = new ConcurrentQueue<string>();

        var loggerProvider = new TestLoggerProvider(observed);

        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton<ILoggerProvider>(loggerProvider);
            },
            serverTlsOptions: new GrpcServerTlsOptions { Certificate = certificate },
            serverRuntimeOptions: new GrpcServerRuntimeOptions
            {
                EnableHttp3 = true,
                Http3 = new Http3RuntimeOptions
                {
                    IdleTimeout = TimeSpan.FromSeconds(10),
                    KeepAliveInterval = TimeSpan.FromSeconds(2),
                    MaxBidirectionalStreams = 4,
                    MaxUnidirectionalStreams = 2
                }
            });

        var options = new DispatcherOptions(ServiceName);
        options.AddLifecycle("grpc-http3-logs", inbound);
        var dispatcher = new Dispatcher.Dispatcher(options);

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);
        await dispatcher.StopOrThrowAsync(CancellationToken.None);

        AssertLogContains(observed, "gRPC HTTP/3 enabled on");
    }

    private static async Task ExerciseGeneratedClientAsync(TestServiceOmniRelay.TestServiceClient client, GeneratedTestService impl, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var meta = new RequestMeta(
            service: ServiceName,
            procedure: "UnaryCall",
            encoding: "protobuf",
            transport: GrpcTransportName,
            timeToLive: TimeSpan.FromMilliseconds(500),
            deadline: deadline);

        var unaryResult = await client.UnaryCallAsync(new UnaryRequest { Message = "ping" }, meta, cancellationToken);
        Assert.True(unaryResult.IsSuccess, unaryResult.Error?.Message);
        Assert.Equal("ping-unary-response", unaryResult.Value.Body.Message);

        var unaryMeta = await impl.UnaryMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(meta.TimeToLive, unaryMeta.TimeToLive);
        Assert.True(Math.Abs((unaryMeta.Deadline!.Value - deadline).TotalMilliseconds) <= DeadlineTolerance.TotalMilliseconds);
        Assert.Equal(GrpcTransportName, unaryMeta.Transport);

        var serverStreamMeta = meta with { Procedure = "ServerStream" };
        var responses = new List<string>();
        await foreach (var response in client.ServerStreamAsync(new StreamRequest { Value = "data" }, serverStreamMeta, cancellationToken))
        {
            responses.Add(response.ValueOrThrow().Body.Value);
        }
        Assert.Equal(new[] { "data#0", "data#1", "data#2" }, responses);
        var capturedServerMeta = await impl.ServerStreamMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(serverStreamMeta.TimeToLive, capturedServerMeta.TimeToLive);

        var clientStreamMeta = meta with { Procedure = "ClientStream" };
        var clientStreamResult = await client.ClientStreamAsync(clientStreamMeta, cancellationToken);
        await using (var session = clientStreamResult.ValueOrThrow())
        {
            (await session.WriteAsync(new StreamRequest { Value = "2" }, cancellationToken)).ThrowIfFailure();
            (await session.WriteAsync(new StreamRequest { Value = "5" }, cancellationToken)).ThrowIfFailure();
            await session.CompleteAsync(cancellationToken);
            var aggregate = (await session.Response).ValueOrThrow();
            Assert.Equal("sum:7", aggregate.Body.Message);
        }

        var capturedClientMeta = await impl.ClientStreamMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(Math.Abs((capturedClientMeta.Deadline!.Value - clientStreamMeta.Deadline!.Value).TotalMilliseconds) <= DeadlineTolerance.TotalMilliseconds);

        var duplexMeta = meta with { Procedure = "DuplexStream" };
        var duplexResult = await client.DuplexStreamAsync(duplexMeta, cancellationToken);
        await using (var session = duplexResult.ValueOrThrow())
        {
            (await session.WriteAsync(new StreamRequest { Value = "alpha" }, cancellationToken)).ThrowIfFailure();
            (await session.WriteAsync(new StreamRequest { Value = "beta" }, cancellationToken)).ThrowIfFailure();
            await session.CompleteRequestsAsync(cancellationToken: cancellationToken);

            var duplexResponses = new List<string>();
            await foreach (var response in session.ReadResponsesAsync(cancellationToken))
            {
                duplexResponses.Add(response.ValueOrThrow().Body.Value);
            }

            Assert.Equal(new[] { "ready", "echo:alpha", "echo:beta" }, duplexResponses);
        }

        var capturedDuplexMeta = await impl.DuplexMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(duplexMeta.Transport, capturedDuplexMeta.Transport);
    }

    private static void AssertLogContains(ConcurrentQueue<string> log, string marker)
    {
        Assert.Contains(log, entry => entry.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertLogContains(IEnumerable<string> log, string marker)
    {
        Assert.Contains(log, entry => entry.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(address.Host, address.Port).WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                await Task.Delay(50, cancellationToken);
                return;
            }
            catch
            {
                await Task.Delay(25, cancellationToken);
            }
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
    }

    private sealed class RecordingServerInterceptor(string id, ConcurrentQueue<string> log) : Interceptor
    {
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            log.Enqueue($"{id}:{context.Method}");
            return await continuation(request, context).ConfigureAwait(false);
        }

        public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
            TRequest request,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            log.Enqueue($"{id}:{context.Method}");
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }

        public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream,
            ServerCallContext context,
            ClientStreamingServerMethod<TRequest, TResponse> continuation)
        {
            log.Enqueue($"{id}:{context.Method}");
            return await continuation(requestStream, context).ConfigureAwait(false);
        }

        public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            log.Enqueue($"{id}:{context.Method}");
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
    }

    private sealed class RecordingClientInterceptor(string id, ConcurrentQueue<string> log) : Interceptor
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            log.Enqueue($"{id}:{context.Method.FullName}");
            return continuation(request, context);
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            log.Enqueue($"{id}:{context.Method.FullName}");
            return continuation(request, context);
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            log.Enqueue($"{id}:{context.Method.FullName}");
            return continuation(context);
        }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            log.Enqueue($"{id}:{context.Method.FullName}");
            return continuation(context);
        }
    }

    private sealed class ProtocolCaptureInterceptor(ConcurrentQueue<string> observed) : Interceptor
    {
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            observed.Enqueue(context.GetHttpContext().Request.Protocol);
            return await base.UnaryServerHandler(request, context, continuation).ConfigureAwait(false);
        }
    }

    private sealed class TestLoggerProvider(ConcurrentQueue<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, log);

        public void Dispose()
        {
        }

        private sealed class ListLogger(string category, ConcurrentQueue<string> log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var message = formatter(state, exception);
                log.Enqueue($"{category}:{message}");
            }
        }
    }
}
