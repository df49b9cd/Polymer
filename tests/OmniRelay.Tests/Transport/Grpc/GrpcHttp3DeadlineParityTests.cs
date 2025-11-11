using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Tests.Support;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Grpc;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Transport.Grpc;

public class GrpcHttp3DeadlineParityTests
{
    [Http3Fact(Timeout = 30_000)]
    public async Task Grpc_Http3_DeadlineExceeded_Matches_Http2()
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
        await dispatcher.StartOrThrowAsync(ct);
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

        var h3Ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var call = invokerH3.AsyncUnaryCall(method, null, new CallOptions(deadline: DateTime.UtcNow.AddMilliseconds(100)), []);
            _ = await call.ResponseAsync;
        });
        Assert.Equal(StatusCode.DeadlineExceeded, h3Ex.StatusCode);

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

        var h2Ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var call = invokerH2.AsyncUnaryCall(method, null, new CallOptions(deadline: DateTime.UtcNow.AddMilliseconds(100)), []);
            _ = await call.ResponseAsync;
        });
        Assert.Equal(StatusCode.DeadlineExceeded, h2Ex.StatusCode);

        // Unblock server and stop
        block.TrySetCanceled(ct);
        await dispatcher.StopOrThrowAsync(ct);
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        using var client = new System.Net.Sockets.TcpClient();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                await client.ConnectAsync(address.Host, address.Port).WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                await Task.Delay(50, cancellationToken);
                return;
            }
            catch
            {
                await Task.Delay(25, cancellationToken);
            }
        }
        throw new TimeoutException("gRPC inbound did not bind in time");
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
