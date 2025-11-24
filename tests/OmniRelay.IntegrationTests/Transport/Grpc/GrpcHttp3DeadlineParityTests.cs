using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Grpc;
using Xunit;
using static AwesomeAssertions.FluentActions;
using static Hugo.Go;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport.Grpc;

public class GrpcHttp3DeadlineParityTests(ITestOutputHelper output) : TransportIntegrationTest(output)
{
    [Http3Fact(Timeout = 30_000)]
    public async ValueTask Grpc_Http3_DeadlineExceeded_Matches_Http2()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-grpc-deadline");

        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"https://127.0.0.1:{port}");

        var serverRuntime = new GrpcServerRuntimeOptions { EnableHttp3 = true };
        var tls = new GrpcServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("grpc-deadline");
        var inbound = new GrpcInbound([address.ToString()], serverTlsOptions: tls, serverRuntimeOptions: serverRuntime);
        options.AddLifecycle("grpc-deadline-in", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Register(new UnaryProcedureSpec(
            "grpc-deadline",
            "grpc-deadline::slow",
            async (_, token) =>
            {
                await block.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var dispatcherHost = await StartDispatcherAsync(nameof(Grpc_Http3_DeadlineExceeded_Matches_Http2), dispatcher, ct);
        await WaitForGrpcReadyAsync(address, ct);

        // HTTP/3 client with tight deadline
        using var h3Handler = CreateHttp3SocketsHandler();
        using var h3Client = new HttpClient(h3Handler, disposeHandler: false)
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var h3Channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = h3Client });
        var invokerH3 = h3Channel.CreateCallInvoker();
        var method = new Method<byte[], byte[]>(MethodType.Unary, "grpc-deadline", "grpc-deadline::slow", GrpcMarshallerCache.ByteMarshaller, GrpcMarshallerCache.ByteMarshaller);

        var h3Ex = await Invoking(async () =>
            {
                using var call = invokerH3.AsyncUnaryCall(method, null, new CallOptions(deadline: DateTime.UtcNow.AddMilliseconds(100)), []);
                _ = await call.ResponseAsync;
            })
            .Should().ThrowAsync<RpcException>();
        h3Ex.Which.StatusCode.Should().Be(StatusCode.DeadlineExceeded);

        // HTTP/2 client with same deadline
        using var h2Handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http2]
            }
        };
        using var h2Client = new HttpClient(h2Handler, disposeHandler: false)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var h2Channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = h2Client });
        var invokerH2 = h2Channel.CreateCallInvoker();

        var h2Ex = await Invoking(async () =>
            {
                using var call = invokerH2.AsyncUnaryCall(method, null, new CallOptions(deadline: DateTime.UtcNow.AddMilliseconds(100)), []);
                _ = await call.ResponseAsync;
            })
            .Should().ThrowAsync<RpcException>();
        h2Ex.Which.StatusCode.Should().Be(StatusCode.DeadlineExceeded);

        // Unblock server and stop
        block.TrySetCanceled(ct);
    }

    private static SocketsHttpHandler CreateHttp3SocketsHandler() => new()
    {
        AllowAutoRedirect = false,
        EnableMultipleHttp3Connections = true,
        SslOptions =
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http3, SslApplicationProtocol.Http2]
        }
    };

}
