using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests;
using OmniRelay.Tests.Protos;
using OmniRelay.Transport.Grpc;
using Xunit;

namespace OmniRelay.CodeGen.IntegrationTests;

public class GrpcCodegenIntegrationTests
{
    private const string ServiceName = "grpc-integration";

    [Fact(Timeout = 30_000)]
    public async Task GeneratedServiceHonorsProtobufCodegen()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions(ServiceName);
        var inbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-generated-inbound", inbound);
        var dispatcher = new Dispatcher.Dispatcher(options);
        dispatcher.RegisterTestService(new GeneratedTestService());

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            using var channel = GrpcChannel.ForAddress(address);
            var invoker = channel.CreateCallInvoker();
            var marshaller = Marshallers.Create(
                (byte[] payload) => payload ?? [],
                payload => payload ?? []);
            var method = new Method<byte[], byte[]>(MethodType.Unary, ServiceName, "UnaryCall", marshaller, marshaller);

            var metadata = new Metadata
            {
                { "rpc-service", ServiceName },
                { "rpc-procedure", "UnaryCall" },
                { "rpc-encoding", "protobuf" }
            };

            var call = invoker.AsyncUnaryCall(method, null, new CallOptions(metadata, cancellationToken: ct), []);
            var response = await call.ResponseAsync.WaitAsync(ct);
            Assert.NotNull(response);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
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
                await Task.Delay(25, cancellationToken);
            }
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
    }
}
