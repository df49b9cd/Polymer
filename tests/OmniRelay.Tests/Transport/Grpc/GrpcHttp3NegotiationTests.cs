using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Grpc.Interceptors;
using Xunit;

using static Hugo.Go;

namespace OmniRelay.Tests.Transport.Grpc;

public class GrpcHttp3NegotiationTests
{
    [Fact(Timeout = 45_000)]
    public async Task GrpcInbound_WithHttp3Enabled_ExecutesInterceptorsOverHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-grpc-http3");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new ConcurrentQueue<string>();
        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = new[] { typeof(AuthorizationInterceptor), typeof(GrpcServerLoggingInterceptor) }
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
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3SocketsHandler();
        using var client = CreateHttp3Client(handler, HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = client });
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http3", "grpc-http3::ping", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var metadata = new Metadata { { "authorization", "Bearer test-token" } };

        try
        {
            var call = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
            var response = await call.ResponseAsync.WaitAsync(ct);
            Assert.Empty(response);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }

        Assert.True(observedProtocols.TryDequeue(out var protocol), "No HTTP protocol was observed by the interceptor.");
        Assert.StartsWith("HTTP/3", protocol, StringComparison.Ordinal);
    }

    [Fact(Timeout = 45_000)]
    public async Task GrpcInbound_WithHttp3Disabled_FallsBackToHttp2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-grpc-http2");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new ConcurrentQueue<string>();
        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = false,
            Interceptors = new[] { typeof(AuthorizationInterceptor) }
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
        options.AddLifecycle("grpc-http2-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-http2",
            "grpc-http2::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3SocketsHandler();
        using var client = CreateHttp3Client(handler, HttpVersionPolicy.RequestVersionOrLower);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = client });
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http2", "grpc-http2::ping", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var metadata = new Metadata { { "authorization", "Bearer test-token" } };

        try
        {
            var call = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
            var response = await call.ResponseAsync.WaitAsync(ct);
            Assert.Empty(response);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }

        Assert.True(observedProtocols.TryDequeue(out var protocol), "No HTTP protocol was observed by the interceptor.");
        Assert.StartsWith("HTTP/2", protocol, StringComparison.Ordinal);
    }

    [Fact(Timeout = 45_000)]
    public async Task GrpcInbound_WithHttp3Enabled_DrainRejectsNewCallsWithRetryAfter()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-grpc-http3-drain");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observedProtocols = new ConcurrentQueue<string>();
        var runtime = new GrpcServerRuntimeOptions
        {
            EnableHttp3 = true,
            Interceptors = new[] { typeof(AuthorizationInterceptor) }
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
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        using var handler = CreateHttp3SocketsHandler();
        using var client = CreateHttp3Client(handler, HttpVersionPolicy.RequestVersionExact);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = client });
        var invoker = channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-http3-drain", "grpc-http3-drain::slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);
        var metadata = new Metadata { { "authorization", "Bearer test-token" } };

        Task? stopTask = null;
        try
        {
            var inFlightCall = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
            await requestStarted.Task.WaitAsync(ct);

            stopTask = dispatcher.StopAsync(ct);
            await Task.Delay(100, ct);

            var rejectedCall = invoker.AsyncUnaryCall(method, null, new CallOptions(headers: metadata), []);
            var rejection = await Assert.ThrowsAsync<RpcException>(() => rejectedCall.ResponseAsync);
            Assert.Equal(StatusCode.Unavailable, rejection.StatusCode);
            Assert.Equal("1", rejection.Trailers.GetValue("retry-after"));

            releaseRequest.TrySetResult();
            var response = await inFlightCall.ResponseAsync.WaitAsync(ct);
            Assert.Empty(response);

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
                await dispatcher.StopAsync(ct);
            }
        }

        Assert.True(observedProtocols.TryDequeue(out var protocol), "No HTTP protocol was observed by the interceptor.");
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
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port)
                            .WaitAsync(TimeSpan.FromMilliseconds(connectTimeoutMilliseconds), cancellationToken)
                            .ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromMilliseconds(settleDelayMilliseconds), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken).ConfigureAwait(false);
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
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http3,
                    SslApplicationProtocol.Http2
                }
            }
        };

        return handler;
    }

    private static HttpClient CreateHttp3Client(SocketsHttpHandler handler, HttpVersionPolicy policy)
    {
        var client = new HttpClient(handler, disposeHandler: false)
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = policy
        };

        return client;
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

            return await continuation(request, context).ConfigureAwait(false);
        }
    }
}
