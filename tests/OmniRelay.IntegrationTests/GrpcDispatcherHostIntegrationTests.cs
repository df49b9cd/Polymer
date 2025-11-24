using System.Net.Mime;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Dispatcher.Config;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class GrpcDispatcherHostIntegrationTests
{
    private static readonly Marshaller<byte[]> ByteMarshaller = Marshallers.Create(
        serializer: static payload => payload ?? [],
        deserializer: static payload => payload ?? []);

    [Fact(Timeout = 30_000)]
    public async ValueTask GrpcInbound_ConfiguredViaHost_RoundtripsUnaryRequest()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "integration-grpc",
            ["omnirelay:inbounds:grpc:0:urls:0"] = address.ToString()
        });

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcherFromConfiguration(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        dispatcher.Register(new UnaryProcedureSpec(
            "integration-grpc",
            "integration::ping",
            static (request, _) =>
            {
                var input = Encoding.UTF8.GetString(request.Body.Span);
                var responseBytes = Encoding.UTF8.GetBytes($"{input}-grpc-response");
                var meta = new ResponseMeta(encoding: MediaTypeNames.Text.Plain);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(responseBytes, meta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await host.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            using var channel = GrpcChannel.ForAddress(address);
            var invoker = channel.CreateCallInvoker();
            var method = new Method<byte[], byte[]>(MethodType.Unary, "integration-grpc", "integration::ping", ByteMarshaller, ByteMarshaller);

            var call = invoker.AsyncUnaryCall(method, null, new CallOptions(cancellationToken: ct), "ping"u8.ToArray());
            var payload = await call.ResponseAsync.WaitAsync(ct);
            Encoding.UTF8.GetString(payload).Should().Be("ping-grpc-response");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
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
                            .WaitAsync(TimeSpan.FromMilliseconds(connectTimeoutMilliseconds), cancellationToken);

                await Task.Delay(TimeSpan.FromMilliseconds(settleDelayMilliseconds), cancellationToken);
                return;
            }
            catch (SocketException)
            {
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
    }
}
