using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc;
using Xunit;

using static Hugo.Go;

namespace OmniRelay.Tests.Transport.Grpc;

public class GrpcOutboundResilienceTests
{
    [Fact(Timeout = 60_000)]
    public async Task Breaker_Trips_On_Http3_Handshake_Failures()
    {
        // Server supports only HTTP/2
        using var certificate = CreateSelfSignedCertificate("CN=omnirelay-grpc-resilience-h2");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var serverRuntime = new GrpcServerRuntimeOptions { EnableHttp3 = false };
        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("grpc-resilience-h2");
        var inbound = new GrpcInbound([address.ToString()], serverTlsOptions: tls, serverRuntimeOptions: serverRuntime);
        options.AddLifecycle("grpc-inbound-resilience-h2", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-resilience-h2",
            "grpc-resilience-h2::ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        // Client requires HTTP/3 exact (handshake will fail consistently)
        var outbound = new GrpcOutbound(
            new[] { address },
            remoteService: "grpc-resilience-h2",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                RequestVersion = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            },
            peerCircuitBreakerOptions: new PeerCircuitBreakerOptions
            {
                FailureThreshold = 3,
                BaseDelay = TimeSpan.FromMilliseconds(10),
                MaxDelay = TimeSpan.FromMilliseconds(50)
            });

        try
        {
            await outbound.StartAsync(ct);

            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfig("grpc-resilience-h2").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-resilience-h2", "grpc-resilience-h2::ping"), Array.Empty<byte>());

            // Issue several attempts to exceed breaker threshold
            for (var i = 0; i < 5; i++)
            {
                var result = await client.CallAsync(request, ct);
                Assert.True(result.IsFailure);
            }

            // Inspect outbound diagnostics for peer state and failure counts
            var snapshot = (GrpcOutboundSnapshot)outbound.GetOutboundDiagnostics()!;
            Assert.Single(snapshot.PeerSummaries);
            var peer = snapshot.PeerSummaries[0];
            Assert.True(peer.FailureCount >= 3, $"Expected at least 3 failures, observed {peer.FailureCount}");
            Assert.Equal(PeerState.Unavailable, peer.State);
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Breaker_Trips_On_Transport_Errors_No_Server()
    {
        // No server listening at this HTTPS endpoint
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var ct = TestContext.Current.CancellationToken;

        var outbound = new GrpcOutbound(
            new[] { address },
            remoteService: "grpc-resilience-noserver",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                // Allow fallback in handler; the absence of a server will still produce transport errors
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            },
            peerCircuitBreakerOptions: new PeerCircuitBreakerOptions
            {
                FailureThreshold = 3,
                BaseDelay = TimeSpan.FromMilliseconds(10),
                MaxDelay = TimeSpan.FromMilliseconds(50)
            });

        try
        {
            await outbound.StartAsync(ct);

            var codec = new RawCodec();
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, Array.Empty<IUnaryOutboundMiddleware>());
            var request = new Request<byte[]>(new RequestMeta("grpc-resilience-noserver", "grpc-resilience-noserver::ping"), Array.Empty<byte>());

            for (var i = 0; i < 5; i++)
            {
                var result = await client.CallAsync(request, ct);
                Assert.True(result.IsFailure);
            }

            var snapshot = (GrpcOutboundSnapshot)outbound.GetOutboundDiagnostics()!;
            Assert.Single(snapshot.PeerSummaries);
            var peer = snapshot.PeerSummaries[0];
            Assert.True(peer.FailureCount >= 3);
            Assert.Equal(PeerState.Unavailable, peer.State);
        }
        finally
        {
            await outbound.StopAsync(ct);
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
}
