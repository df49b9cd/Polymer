using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Polymer.Core;
using Polymer.Core.Clients;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Errors;
using Polymer.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace Polymer.Tests.Transport;

public class HttpTransportTests
{
    [Fact]
    public async Task UnaryRoundtrip_EncodesAndDecodesPayload()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("echo");
        var httpInbound = new HttpInbound(new[] { baseAddress.ToString() });
        options.AddLifecycle("http-inbound", httpInbound);

        var httpClient = new HttpClient { BaseAddress = baseAddress };
        var httpOutbound = new HttpOutbound(httpClient, baseAddress, disposeClient: true);
        options.AddUnaryOutbound("echo", null, httpOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

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

                var responsePayload = new EchoResponse { Message = decodeResult.Value.Message.ToUpperInvariant() };
                var encodeResult = codec.EncodeResponse(responsePayload, new ResponseMeta(encoding: "application/json"));
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
            transport: "http");

        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("hello"));

        var result = await client.CallAsync(request, ct);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("HELLO", result.Value.Body.Message);

        await dispatcher.StopAsync(ct);
    }

    private sealed record EchoRequest(string Message);

    private sealed record EchoResponse
    {
        public string Message { get; init; } = string.Empty;
    }
}
