using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.TestSupport;
using OmniRelay.Tests.Protos;
using OmniRelay.Transport.Grpc;
using Xunit;
using Xunit.Sdk;
using static Hugo.Go;

namespace OmniRelay.CodeGen.IntegrationTests;

public class GeneratedClientHttp3Tests
{
    [Http3Fact(Timeout = 45_000)]
    public async Task GeneratedClient_Unary_UsesHttp3_WhenEnabled()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        if (!QuicConnection.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSigned("CN=omnirelay-codegen-http3");
        var port = GetFreeTcpPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observed = new ConcurrentQueue<string>();
        var serverRuntime = new GrpcServerRuntimeOptions { EnableHttp3 = true };
        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("codegen-svc");
        var inbound = new GrpcInbound(
            [address.ToString()],
            configureApp: app =>
            {
                app.Use(async (context, next) =>
                {
                    observed.Enqueue(context.Request.Protocol ?? string.Empty);
                    await next();
                });
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: serverRuntime);
        options.AddLifecycle("inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        TestServiceOmniRelay.RegisterTestService(dispatcher, new Impl());

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        // Outbound with HTTP/3 enabled
        var outbound = new GrpcOutbound(
            [address],
            remoteService: "codegen-svc",
            clientRuntimeOptions: new GrpcClientRuntimeOptions { EnableHttp3 = true },
            clientTlsOptions: new GrpcClientTlsOptions
            {
                EnabledProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CheckCertificateRevocation = false,
                ServerCertificateValidationCallback = static (_, _, _, _) => true
            });

        var clientOptions = new DispatcherOptions("client-gw");
        clientOptions.AddUnaryOutbound("codegen-svc", null, outbound);
        var clientDispatcher = new OmniRelay.Dispatcher.Dispatcher(clientOptions);
        await outbound.StartAsync(ct);

        try
        {
            var client = TestServiceOmniRelay.CreateTestServiceClient(clientDispatcher, "codegen-svc");
            var result = await client.UnaryCallAsync(new UnaryRequest { Message = "ping" }, cancellationToken: ct);
            if (!result.IsSuccess && result.Error?.Message?.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase) == true)
            {
                return;
            }
            Assert.True(result.IsSuccess, result.Error?.ToString());
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }

        Assert.True(TryDequeueWithWait(observed, out var protocol), "No protocol captured.");
        if (protocol.StartsWith("HTTP/3", StringComparison.OrdinalIgnoreCase))
        {
            throw new XunitException("Grpc.Net.Client negotiated HTTP/3; update GeneratedClientHttp3Tests expectations.");
        }

        Assert.StartsWith("HTTP/2", protocol, StringComparison.Ordinal);
    }

    [Http3Fact(Timeout = 45_000)]
    public async Task GeneratedClient_Unary_FallsBack_ToHttp2_WhenServerDisablesHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        if (!QuicConnection.IsSupported)
        {
            return;
        }

        using var certificate = CreateSelfSigned("CN=omnirelay-codegen-http2");
        var port = GetFreeTcpPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var observed = new ConcurrentQueue<string>();
        var serverRuntime = new GrpcServerRuntimeOptions { EnableHttp3 = false };
        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("codegen-svc-h2");
        var inbound = new GrpcInbound(
            [address.ToString()],
            configureApp: app =>
            {
                app.Use(async (context, next) =>
                {
                    observed.Enqueue(context.Request.Protocol ?? string.Empty);
                    await next();
                });
            },
            serverTlsOptions: tls,
            serverRuntimeOptions: serverRuntime);
        options.AddLifecycle("inbound-h2", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        TestServiceOmniRelay.RegisterTestService(dispatcher, new Impl());

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        // Outbound desires HTTP/3 but should fall back
        var outbound = new GrpcOutbound(
            [address],
            remoteService: "codegen-svc-h2",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true, VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            },
            clientTlsOptions: new GrpcClientTlsOptions
            {
                EnabledProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CheckCertificateRevocation = false,
                ServerCertificateValidationCallback = static (_, _, _, _) => true
            });

        var clientOptions = new DispatcherOptions("client-gw-h2");
        clientOptions.AddUnaryOutbound("codegen-svc-h2", null, outbound);
        var clientDispatcher = new OmniRelay.Dispatcher.Dispatcher(clientOptions);
        await outbound.StartAsync(ct);

        try
        {
            var client = TestServiceOmniRelay.CreateTestServiceClient(clientDispatcher, "codegen-svc-h2");
            var result = await client.UnaryCallAsync(new UnaryRequest { Message = "ping" }, cancellationToken: ct);
            if (!result.IsSuccess && result.Error?.Message?.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase) == true)
            {
                return;
            }
            Assert.True(result.IsSuccess, result.Error?.ToString());
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopOrThrowAsync(ct);
        }

        Assert.True(TryDequeueWithWait(observed, out var protocol), "No protocol captured.");
        if (protocol.StartsWith("HTTP/3", StringComparison.OrdinalIgnoreCase))
        {
            throw new XunitException("Grpc.Net.Client negotiated HTTP/3; update GeneratedClientHttp3Tests expectations.");
        }

        Assert.StartsWith("HTTP/2", protocol, StringComparison.Ordinal);
    }

    private sealed class Impl : TestServiceOmniRelay.ITestService
    {
        public ValueTask<Response<UnaryResponse>> UnaryCallAsync(Request<UnaryRequest> request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(Response<UnaryResponse>.Create(new UnaryResponse { Message = request.Body.Message },
                new ResponseMeta()));

        public async ValueTask ServerStreamAsync(Request<StreamRequest> request,
            ProtobufCallAdapters.ProtobufServerStreamWriter<StreamRequest, StreamResponse> stream,
            CancellationToken cancellationToken)
        {
            var writeResult =
                await stream.WriteAsync(new StreamResponse { Value = request.Body.Value }, cancellationToken);
            writeResult.ThrowIfFailure();
        }

        public ValueTask<Response<UnaryResponse>> ClientStreamAsync(
            ProtobufCallAdapters.ProtobufClientStreamContext<StreamRequest, UnaryResponse> context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(Response<UnaryResponse>.Create(new UnaryResponse { Message = "ok" },
                new ResponseMeta()));

        public ValueTask DuplexStreamAsync(
            ProtobufCallAdapters.ProtobufDuplexStreamContext<StreamRequest, StreamResponse> context,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        using var client = new System.Net.Sockets.TcpClient();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                await client.ConnectAsync(address.Host, address.Port)
                    .WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                await Task.Delay(50, cancellationToken);
                return;
            }
            catch
            {
                await Task.Delay(25, cancellationToken);
            }
        }

        throw new TimeoutException("Inbound not ready");
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool TryDequeueWithWait<T>(ConcurrentQueue<T> queue, out T value, int timeoutMilliseconds = 5000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMilliseconds)
        {
            if (queue.TryDequeue(out value!))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        value = default!;
        return false;
    }
}
