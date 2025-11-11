using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class HttpOutboundIntegrationTests
{
    [Http3Fact(Timeout = 45_000)]
    public async Task HttpOutbound_WithHttp3Preferred_FallsBackToHttp2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http2-outbound-integration");

        var port = TestPortAllocator.GetRandomPort();
        var remoteAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions { EnableHttp3 = false };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var remoteOptions = new DispatcherOptions("runtime-remote");
        var remoteInbound = new HttpInbound([remoteAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        remoteOptions.AddLifecycle("runtime-remote-http", remoteInbound);

        var remoteDispatcher = new OmniRelay.Dispatcher.Dispatcher(remoteOptions);
        remoteDispatcher.Register(new UnaryProcedureSpec(
            "runtime-remote",
            "runtime::ping",
            (request, _) =>
            {
                var payload = "{\"message\":\"pong\"}"u8.ToArray();
                var responseMeta = new ResponseMeta(encoding: MediaTypeNames.Application.Json);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, responseMeta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await remoteDispatcher.StartOrThrowAsync(ct);
        await WaitForEndpointReadyAsync(remoteAddress, ct);

        using var handler = CreateHttp3SocketsHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = remoteAddress };
        var httpOutbound = new HttpOutbound(
            httpClient,
            remoteAddress,
            disposeClient: false,
            runtimeOptions: new HttpClientRuntimeOptions { EnableHttp3 = true });

        var clientOptions = new DispatcherOptions("runtime-client");
        clientOptions.AddUnaryOutbound("runtime-remote", null, httpOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(clientOptions);
        var codec = new JsonCodec<PingRequest, PingResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var client = dispatcher.CreateUnaryClient<PingRequest, PingResponse>("runtime-remote", codec);

        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            var meta = new RequestMeta(
                service: "runtime-remote",
                procedure: "runtime::ping",
                encoding: MediaTypeNames.Application.Json,
                transport: "http");
            var request = new Request<PingRequest>(meta, new PingRequest("ping"));

            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal("pong", result.Value.Body.Message);

            var protocolHeader = result.Value.Meta.Headers.FirstOrDefault(h => string.Equals(h.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase)).Value;
            Assert.Equal("HTTP/2", protocolHeader);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
            await remoteDispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task HttpOutbound_FailoverAcrossPeers_RetriesUntilSuccess()
    {
        var peer1Port = TestPortAllocator.GetRandomPort();
        var peer1Address = new Uri($"http://127.0.0.1:{peer1Port}/");
        var peer2Port = TestPortAllocator.GetRandomPort();
        var peer2Address = new Uri($"http://127.0.0.1:{peer2Port}/");

        var failoverProcedure = "failover::ping";
        var peer1Calls = 0;
        var peer2Calls = 0;

        var peer1Options = new DispatcherOptions("failover-remote");
        var peer1Inbound = new HttpInbound([peer1Address.ToString()]);
        peer1Options.AddLifecycle("peer1-http", peer1Inbound);
        var peer1Dispatcher = new OmniRelay.Dispatcher.Dispatcher(peer1Options);
        peer1Dispatcher.Register(new UnaryProcedureSpec(
            "failover-remote",
            failoverProcedure,
            (request, _) =>
            {
                Interlocked.Increment(ref peer1Calls);
                var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "peer-unavailable", transport: "http");
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            }));

        var peer2Options = new DispatcherOptions("failover-remote");
        var peer2Inbound = new HttpInbound([peer2Address.ToString()]);
        peer2Options.AddLifecycle("peer2-http", peer2Inbound);
        var peer2Dispatcher = new OmniRelay.Dispatcher.Dispatcher(peer2Options);
        peer2Dispatcher.Register(new UnaryProcedureSpec(
            "failover-remote",
            failoverProcedure,
            (request, _) =>
            {
                Interlocked.Increment(ref peer2Calls);
                var payload = "{\"message\":\"peer-b\"}"u8.ToArray();
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, new ResponseMeta(encoding: MediaTypeNames.Application.Json))));
            }));

        var ct = TestContext.Current.CancellationToken;
        await peer1Dispatcher.StartOrThrowAsync(ct);
        await peer2Dispatcher.StartOrThrowAsync(ct);

        using var peer1Client = new HttpClient { BaseAddress = peer1Address };
        using var peer2Client = new HttpClient { BaseAddress = peer2Address };

        var outbound1 = new HttpOutbound(peer1Client, peer1Address, disposeClient: false);
        var outbound2 = new HttpOutbound(peer2Client, peer2Address, disposeClient: false);
        var failoverOutbound = new FailoverUnaryOutbound(outbound1, outbound2);

        var clientOptions = new DispatcherOptions("failover-client");
        clientOptions.AddUnaryOutbound("failover-remote", null, failoverOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(clientOptions);
        var codec = new JsonCodec<PingRequest, PingResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var client = dispatcher.CreateUnaryClient<PingRequest, PingResponse>("failover-remote", codec);

        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            var meta = new RequestMeta(
                service: "failover-remote",
                procedure: failoverProcedure,
                encoding: MediaTypeNames.Application.Json,
                transport: "http");
            var request = new Request<PingRequest>(meta, new PingRequest("ping"));

            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal("peer-b", result.Value.Body.Message);
            Assert.True(peer1Calls >= 1, "Primary peer should receive at least one attempt.");
            Assert.Equal(1, peer2Calls);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
            await peer1Dispatcher.StopOrThrowAsync(CancellationToken.None);
            await peer2Dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForEndpointReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port).WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                await Task.Delay(50, cancellationToken);
                return;
            }
            catch
            {
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException("The HTTP endpoint failed to start within the allotted time.");
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

    private sealed record PingRequest(string Message)
    {
        public string Message { get; init; } = Message;
    }

    private sealed record PingResponse
    {
        public string Message { get; init; } = string.Empty;
    }

    private sealed class FailoverUnaryOutbound : IUnaryOutbound
    {
        private readonly IUnaryOutbound[] _peers;

        public FailoverUnaryOutbound(params IUnaryOutbound[] peers)
        {
            if (peers is null || peers.Length == 0)
            {
                throw new ArgumentException("At least one peer is required.", nameof(peers));
            }

            _peers = peers;
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            foreach (var peer in _peers)
            {
                await peer.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            for (var index = _peers.Length - 1; index >= 0; index--)
            {
                await _peers[index].StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default)
        {
            Result<Response<ReadOnlyMemory<byte>>>? lastFailure = null;

            foreach (var peer in _peers)
            {
                var result = await peer.CallAsync(request, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    return result;
                }

                lastFailure = result;
            }

            return lastFailure ?? Err<Response<ReadOnlyMemory<byte>>>("all peers failed");
        }
    }
}
