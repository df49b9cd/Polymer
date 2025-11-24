using System.Net;
using System.Net.Quic;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Grpc;
using Xunit;
using static Hugo.Go;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport.Grpc;

public sealed class GrpcOutboundHttp3ClientTests(ITestOutputHelper output) : TransportIntegrationTest(output)
{
    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcOutbound_WithHttp3Enabled_UsesHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        if (!QuicConnection.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-outbound-http3");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var serverRuntime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = [typeof(ProtocolCaptureInterceptor)]
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("grpc-outbound-http3");
        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(observedProtocols);
                services.AddSingleton<ProtocolCaptureInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: serverRuntime);
        options.AddLifecycle("grpc-outbound-in", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-outbound-http3",
            "grpc-outbound-http3::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var serverHost = await StartDispatcherAsync(nameof(GrpcOutbound_WithHttp3Enabled_UsesHttp3), dispatcher, ct, ownsLifetime: false);
        await WaitForGrpcReadyAsync(address, ct);

        var outbound = new GrpcOutbound(
            [address],
            remoteService: "grpc-outbound-http3",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            });

        try
        {
            await outbound.StartAsync(ct);
            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfigChecked("grpc-outbound-http3").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-outbound-http3", "grpc-outbound-http3::ping"), []);
            var result = await client.CallAsync(request, ct);
            result.IsSuccess.Should().BeTrue(result.Error?.ToString() ?? "Result was not successful.");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopAsyncChecked(ct);
        }

        observedProtocols.TryDequeue(out var protocol).Should().BeTrue("No HTTP protocol was observed by the server interceptor.");
        protocol.Should().StartWithEquivalentOf("HTTP/3");
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcOutbound_WithOrHigher_ToHttp2Server_DowngradesToHttp2()
    {
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-outbound-http2");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var serverRuntime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = false,
            Interceptors = [typeof(ProtocolCaptureInterceptor)]
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("grpc-outbound-http2");
        var inbound = new GrpcInbound(
            [address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(observedProtocols);
                services.AddSingleton<ProtocolCaptureInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: serverRuntime);
        options.AddLifecycle("grpc-outbound-in-http2", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-outbound-http2",
            "grpc-outbound-http2::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var serverHost = await StartDispatcherAsync(nameof(GrpcOutbound_WithOrHigher_ToHttp2Server_DowngradesToHttp2), dispatcher, ct, ownsLifetime: false);
        await WaitForGrpcReadyAsync(address, ct);

        var outbound = new GrpcOutbound(
            [address],
            remoteService: "grpc-outbound-http2",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            });

        try
        {
            await outbound.StartAsync(ct);
            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfigChecked("grpc-outbound-http2").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-outbound-http2", "grpc-outbound-http2::ping"), []);
            var result = await client.CallAsync(request, ct);
            result.IsSuccess.Should().BeTrue(result.Error?.ToString() ?? "Result was not successful.");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopAsyncChecked(ct);
        }

        observedProtocols.TryDequeue(out var protocol).Should().BeTrue("No HTTP protocol was observed by the server interceptor.");
        protocol.Should().StartWithEquivalentOf("HTTP/2");
    }

    [Http3Fact(Timeout = 45_000)]
    public async ValueTask GrpcOutbound_WithExactHttp3_ToHttp2Server_Fails()
    {
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-outbound-http3-exact");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var serverRuntime = new GrpcServerRuntimeOptions { EnableHttp3 = false };
        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("grpc-outbound-http3-exact");
        var inbound = new GrpcInbound([address.ToString()], serverTlsOptions: tls, serverRuntimeOptions: serverRuntime);
        options.AddLifecycle("grpc-outbound-in-http3-exact", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-outbound-http3-exact",
            "grpc-outbound-http3-exact::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var serverHost = await StartDispatcherAsync(nameof(GrpcOutbound_WithExactHttp3_ToHttp2Server_Fails), dispatcher, ct, ownsLifetime: false);
        await WaitForGrpcReadyAsync(address, ct);

        var outbound = new GrpcOutbound(
            [address],
            remoteService: "grpc-outbound-http3-exact",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                RequestVersion = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                AllowHttp2Fallback = false
            });

        try
        {
            await outbound.StartAsync(ct);
            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfigChecked("grpc-outbound-http3-exact").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-outbound-http3-exact", "grpc-outbound-http3-exact::ping"), []);
            var result = await client.CallAsync(request, ct);
            result.IsFailure.Should().BeTrue("Call should fail when HTTP/3 exact is required but server is HTTP/2 only.");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopAsyncChecked(ct);
        }
    }

    private sealed class ProtocolCaptureInterceptor(System.Collections.Concurrent.ConcurrentQueue<string> observed) : Interceptor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _observed = observed ?? throw new ArgumentNullException(nameof(observed));

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var httpContext = context.GetHttpContext();
            if (httpContext is not null)
            {
                _observed.Enqueue(httpContext.Request.Protocol);
            }

            return await continuation(request, context);
        }
    }
}
