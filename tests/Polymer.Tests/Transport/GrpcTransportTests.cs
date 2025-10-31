using System;
using System.Text.Json;
using System.Threading.Tasks;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Transport.Grpc;
using Xunit;
using static Hugo.Go;

namespace Polymer.Tests.Transport;

public class GrpcTransportTests
{
    [Fact]
    public async Task UnaryRoundtrip_UsesGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound(new[] { address.ToString() });
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address);
        options.AddUnaryOutbound("echo", null, grpcOutbound);

        var dispatcher = new Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "ping",
            async (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!);
                }

                var payload = new EchoResponse { Message = decodeResult.Value.Message + "-grpc" };
                var encodeResult = codec.EncodeResponse(payload, new ResponseMeta(encoding: "application/json"));
                if (encodeResult.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!);
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, new ResponseMeta(encoding: "application/json"));
                return Ok(response);
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        var client = dispatcher.CreateUnaryClient<EchoRequest, EchoResponse>("echo", codec);

        var requestMeta = new RequestMeta(
            service: "echo",
            procedure: "ping",
            encoding: "application/json",
            transport: "grpc");

        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("hello"));

        var result = await client.CallAsync(request, ct);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("hello-grpc", result.Value.Body.Message);

        await dispatcher.StopAsync(ct);
    }

    private sealed record EchoRequest(string Message);

    private sealed record EchoResponse
    {
        public string Message { get; init; } = string.Empty;
    }
}
