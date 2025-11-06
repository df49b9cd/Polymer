using System.Net.Sockets;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Tests.Protos;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Codegen;

public class GeneratedServiceIntegrationTests
{
    [Fact]
    public async Task Unary_Http_Roundtrips()
    {
        var httpPort = TestPortAllocator.GetRandomPort();
        var serviceName = "yarpcore.tests.codegen";
        var address = new Uri($"http://127.0.0.1:{httpPort}");

        var options = new DispatcherOptions(serviceName);
        var httpInbound = new HttpInbound([address.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var httpClient = new HttpClient { BaseAddress = address };
        var httpOutbound = new HttpOutbound(httpClient, address, disposeClient: true);
        options.AddUnaryOutbound(serviceName, null, httpOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.RegisterTestService(new TestServiceImpl());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync(cts.Token);
        try
        {
            var client = TestServiceOmniRelay.CreateTestServiceClient(dispatcher, serviceName);
            var response = await client.UnaryCallAsync(new UnaryRequest { Message = "hello" }, cancellationToken: cts.Token);
            Assert.True(response.IsSuccess, response.Error?.Message);
            Assert.Equal("hello-unary", response.Value.Body.Message);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unary_Grpc_Roundtrips()
    {
        var grpcPort = TestPortAllocator.GetRandomPort();
        var serviceName = "yarpcore.tests.codegen";
        var address = new Uri($"http://127.0.0.1:{grpcPort}");

        var options = new DispatcherOptions(serviceName);
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, serviceName);
        options.AddUnaryOutbound(serviceName, null, grpcOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        TestServiceOmniRelay.RegisterTestService(dispatcher, new TestServiceImpl());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync(cts.Token);

        await WaitForGrpcReadyAsync(address, cts.Token);
        await Task.Delay(100, cts.Token);

        try
        {
            var client = TestServiceOmniRelay.CreateTestServiceClient(dispatcher, serviceName);
            var response = await client.UnaryCallAsync(new UnaryRequest { Message = "grpc" }, cancellationToken: cts.Token);
            Assert.True(response.IsSuccess, response.Error?.Message);
            Assert.Equal("grpc-unary", response.Value.Body.Message);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private sealed class TestServiceImpl : TestServiceOmniRelay.ITestService
    {
        public ValueTask<Response<UnaryResponse>> UnaryCallAsync(Request<UnaryRequest> request, CancellationToken cancellationToken)
        {
            var response = new UnaryResponse { Message = request.Body.Message + "-unary" };
            return new ValueTask<Response<UnaryResponse>>(Response<UnaryResponse>.Create(response, new ResponseMeta(encoding: ProtobufEncoding.Protobuf)));
        }

        public async ValueTask ServerStreamAsync(Request<StreamRequest> request, ProtobufCallAdapters.ProtobufServerStreamWriter<StreamRequest, StreamResponse> stream, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(new StreamResponse { Value = request.Body.Value + "-stream-1" }, cancellationToken);
            await stream.WriteAsync(new StreamResponse { Value = request.Body.Value + "-stream-2" }, cancellationToken);
            await stream.CompleteAsync(cancellationToken);
        }

        public async ValueTask<Response<UnaryResponse>> ClientStreamAsync(ProtobufCallAdapters.ProtobufClientStreamContext<StreamRequest, UnaryResponse> context, CancellationToken cancellationToken)
        {
            var values = new List<string>();
            await foreach (var message in context.ReadAllAsync(cancellationToken))
            {
                values.Add(message.Value);
            }

            var response = new UnaryResponse { Message = string.Join(",", values.Select(v => v + "-client")) };
            return Response<UnaryResponse>.Create(response, new ResponseMeta(encoding: ProtobufEncoding.Protobuf));
        }

        public async ValueTask DuplexStreamAsync(ProtobufCallAdapters.ProtobufDuplexStreamContext<StreamRequest, StreamResponse> context, CancellationToken cancellationToken)
        {
            await foreach (var message in context.ReadAllAsync(cancellationToken))
            {
                await context.WriteAsync(new StreamResponse { Value = message.Value + "-duplex" }, cancellationToken);
            }

            await context.CompleteResponsesAsync(cancellationToken);
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

}
