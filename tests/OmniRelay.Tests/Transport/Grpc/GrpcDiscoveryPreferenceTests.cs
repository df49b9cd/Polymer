using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Dispatcher;
using OmniRelay.Tests.Support;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Grpc;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Transport.Grpc;

public class GrpcDiscoveryPreferenceTests
{
    [Http3Fact(Timeout = 60_000)]
    public async Task Prefer_Http3_Endpoints_When_Available()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-discovery-pref");

        var h2Port = TestPortAllocator.GetRandomPort();
        var h3Port = TestPortAllocator.GetRandomPort();
        var h2Address = new Uri($"https://127.0.0.1:{h2Port}");
        var h3Address = new Uri($"https://127.0.0.1:{h3Port}");

        var observedProtocols = new System.Collections.Concurrent.ConcurrentQueue<string>();

        // HTTP/2-only inbound
        var h2ServerRuntime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = false,
            Interceptors = [typeof(ProtocolCaptureInterceptor)]
        };

        // HTTP/3-enabled inbound
        var h3ServerRuntime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = [typeof(ProtocolCaptureInterceptor)]
        };

        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("grpc-discovery-pref");
        var inboundH2 = new GrpcInbound(
            [h2Address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(observedProtocols);
                services.AddSingleton<ProtocolCaptureInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: h2ServerRuntime);
        options.AddLifecycle("grpc-inbound-h2", inboundH2);

        var inboundH3 = new GrpcInbound(
            [h3Address.ToString()],
            configureServices: services =>
            {
                services.AddSingleton(observedProtocols);
                services.AddSingleton<ProtocolCaptureInterceptor>();
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: h3ServerRuntime);
        options.AddLifecycle("grpc-inbound-h3", inboundH3);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-discovery-pref",
            "grpc-discovery-pref::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(h2Address, ct);
        await WaitForGrpcReadyAsync(h3Address, ct);

        // Outbound with both endpoints; mark HTTP/3 support for the h3 address only.
        var addresses = new[] { h2Address, h3Address };
        var h3Support = new Dictionary<Uri, bool>
        {
            [h2Address] = false,
            [h3Address] = true
        };

        var outbound = new GrpcOutbound(
            addresses,
            remoteService: "grpc-discovery-pref",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            },
            endpointHttp3Support: h3Support);

        try
        {
            await outbound.StartAsync(ct);
            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfigOrThrow("grpc-discovery-pref").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-discovery-pref", "grpc-discovery-pref::ping"), []);
            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsSuccess, result.Error?.ToString() ?? "Result was not successful.");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }

        Assert.True(observedProtocols.TryDequeue(out var protocol), "No HTTP protocol was observed by the server interceptor.");
        Assert.StartsWith("HTTP/3", protocol, StringComparison.Ordinal);
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
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(address.Host, address.Port)
                            .WaitAsync(TimeSpan.FromMilliseconds(connectTimeoutMilliseconds), cancellationToken);

                await Task.Delay(TimeSpan.FromMilliseconds(settleDelayMilliseconds), cancellationToken);
                return;
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
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
