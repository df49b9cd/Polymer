using System.Net;
using AwesomeAssertions;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Peers;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Grpc;
using Xunit;
using static Hugo.Go;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport.Grpc;

public sealed class GrpcOutboundResilienceTests(ITestOutputHelper output) : TransportIntegrationTest(output)
{
    [Http3Fact(Timeout = 60_000)]
    public async ValueTask Breaker_Trips_On_Http3_Handshake_Failures()
    {
        // Server supports only HTTP/2
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-resilience-h2");

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
        await using var serverHost = await StartDispatcherAsync(nameof(Breaker_Trips_On_Http3_Handshake_Failures), dispatcher, ct, ownsLifetime: false);
        await WaitForGrpcReadyAsync(address, ct);

        // Client requires HTTP/3 exact (handshake will fail consistently)
        var outbound = new GrpcOutbound(
            [address],
            remoteService: "grpc-resilience-h2",
            clientRuntimeOptions: new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true,
                RequestVersion = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                AllowHttp2Fallback = false
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
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, dispatcher.ClientConfigChecked("grpc-resilience-h2").UnaryMiddleware);
            var request = new Request<byte[]>(new RequestMeta("grpc-resilience-h2", "grpc-resilience-h2::ping"), []);

            // Issue several attempts to exceed breaker threshold
            for (var i = 0; i < 5; i++)
            {
                var result = await client.CallAsync(request, ct);
                result.IsFailure.Should().BeTrue();
            }

            // Inspect outbound diagnostics for peer state and failure counts
            var snapshot = (GrpcOutboundSnapshot)outbound.GetOutboundDiagnostics()!;
            snapshot.PeerSummaries.Should().ContainSingle();
            var peer = snapshot.PeerSummaries[0];
            peer.FailureCount.Should().BeGreaterThanOrEqualTo(3, $"Expected at least 3 failures, observed {peer.FailureCount}");
            peer.State.Should().Be(PeerState.Unavailable);
        }
        finally
        {
            await outbound.StopAsync(ct);
            await dispatcher.StopAsyncChecked(ct);
        }
    }

    [Http3Fact(Timeout = 30_000)]
    public async ValueTask Breaker_Trips_On_Transport_Errors_No_Server()
    {
        // No server listening at this HTTPS endpoint
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var ct = TestContext.Current.CancellationToken;

        var outbound = new GrpcOutbound(
            [address],
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
            var client = new UnaryClient<byte[], byte[]>(outbound, codec, []);
            var request = new Request<byte[]>(new RequestMeta("grpc-resilience-noserver", "grpc-resilience-noserver::ping"), []);

            for (var i = 0; i < 5; i++)
            {
                var result = await client.CallAsync(request, ct);
                result.IsFailure.Should().BeTrue();
            }

            var snapshot = (GrpcOutboundSnapshot)outbound.GetOutboundDiagnostics()!;
            snapshot.PeerSummaries.Should().ContainSingle();
            var peer = snapshot.PeerSummaries[0];
            peer.FailureCount.Should().BeGreaterThanOrEqualTo(3);
            peer.State.Should().Be(PeerState.Unavailable);
        }
        finally
        {
            await outbound.StopAsync(ct);
        }
    }

}
