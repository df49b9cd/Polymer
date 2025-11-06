using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc;
using Xunit;

using static Hugo.Go;

namespace OmniRelay.Tests.Transport.Grpc;

public class GrpcOutboundHttp3ClientTests
{
    [Fact(Timeout = 45_000)]
    public async Task GrpcOutbound_WithHttp3Enabled_UsesHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-grpc-outbound-http3");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var serverRuntime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = new[] { typeof(ProtocolCaptureInterceptor) }
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
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var outbound = new GrpcOutbound(
            [address],
            remoteService: "grpc-outbound-http3",
            clientRuntimeOptions: new GrpcClientRuntimeOptions { EnableHttp3 = true });

        try
        {
            await outbound.StartAsync(ct);
            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfig("grpc-outbound-http3").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-outbound-http3", "grpc-outbound-http3::ping"), Array.Empty<byte>());
            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsSuccess);
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopAsync(ct);
        }

        Assert.True(observedProtocols.TryDequeue(out var protocol), "No HTTP protocol was observed by the server interceptor.");
        Assert.StartsWith("HTTP/3", protocol, StringComparison.Ordinal);
    }

    [Fact(Timeout = 45_000)]
    public async Task GrpcOutbound_WithOrHigher_ToHttp2Server_DowngradesToHttp2()
    {
        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-grpc-outbound-http2");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var serverRuntime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = false,
            Interceptors = new[] { typeof(ProtocolCaptureInterceptor) }
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
        await dispatcher.StartAsync(ct);
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
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfig("grpc-outbound-http2").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-outbound-http2", "grpc-outbound-http2::ping"), Array.Empty<byte>());
            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsSuccess);
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopAsync(ct);
        }

        Assert.True(observedProtocols.TryDequeue(out var protocol), "No HTTP protocol was observed by the server interceptor.");
        Assert.StartsWith("HTTP/2", protocol, StringComparison.Ordinal);
    }

    [Fact(Timeout = 45_000)]
    public async Task GrpcOutbound_WithExactHttp3_ToHttp2Server_Fails()
    {
        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-grpc-outbound-http3-exact");

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
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        var outbound = new GrpcOutbound(
            [address],
            remoteService: "grpc-outbound-http3-exact",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                RequestVersion = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            });

        try
        {
            await outbound.StartAsync(ct);
            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfig("grpc-outbound-http3-exact").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-outbound-http3-exact", "grpc-outbound-http3-exact::ping"), Array.Empty<byte>());
            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsFailure, "Call should fail when HTTP/3 exact is required but server is HTTP/2 only.");
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopAsync(ct);
        }
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
