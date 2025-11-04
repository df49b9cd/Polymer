using System.Text.Json;
using Hugo;
using Json.Schema;
using Xunit;
using YARPCore.Core;
using YARPCore.Core.Transport;
using YARPCore.Dispatcher;

using static Hugo.Go;

namespace YARPCore.Tests.Dispatcher;

public class DispatcherJsonExtensionsTests
{
    [Fact]
    public async Task RegisterJsonUnary_RegistersCodecAndHandlesRequest()
    {
        var options = new DispatcherOptions("echo");
        var dispatcher = new YARPCore.Dispatcher.Dispatcher(options);

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        dispatcher.RegisterJsonUnary<JsonEchoRequest, JsonEchoResponse>(
            "echo::greet",
            async (context, request) =>
            {
                await Task.Yield();
                return Response<JsonEchoResponse>.Create(new JsonEchoResponse(request.Name.ToUpperInvariant()), new ResponseMeta());
            },
            codec =>
            {
                codec.Encoding = "application/json";
                codec.SerializerOptions = serializerOptions;
                codec.RequestSchema = JsonSchema.FromText(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" }
                      },
                      "required": ["name"]
                    }
                    """);
            },
            builder => builder.AddAlias("v1::greet"));

        var payload = JsonSerializer.SerializeToUtf8Bytes(new JsonEchoRequest("sandy"), serializerOptions);
        var requestMeta = new RequestMeta(service: "echo", procedure: "echo::greet", transport: "test");
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload);

        var result = await dispatcher.InvokeUnaryAsync("echo::greet", request, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.ToString() ?? "unknown error");

        var decoded = JsonSerializer.Deserialize<JsonEchoResponse>(result.Value.Body.ToArray(), serializerOptions);
        Assert.Equal("SANDY", decoded?.Message);
        Assert.Equal("application/json", result.Value.Meta.Encoding);

        var aliasMeta = requestMeta with { Procedure = "v1::greet" };
        var aliasRequest = new Request<ReadOnlyMemory<byte>>(aliasMeta, payload);
        var aliasResult = await dispatcher.InvokeUnaryAsync("v1::greet", aliasRequest, CancellationToken.None);
        Assert.True(aliasResult.IsSuccess);

        Assert.True(dispatcher.Codecs.TryResolve<JsonEchoRequest, JsonEchoResponse>(
            ProcedureCodecScope.Inbound,
            "echo",
            "echo::greet",
            ProcedureKind.Unary,
            out var inboundCodec));
        Assert.Equal("application/json", inboundCodec.Encoding);

        Assert.True(dispatcher.Codecs.TryResolve<JsonEchoRequest, JsonEchoResponse>(
            ProcedureCodecScope.Inbound,
            "echo",
            "v1::greet",
            ProcedureKind.Unary,
            out _));
    }

    [Fact]
    public async Task CreateJsonClient_UsesCodecRegistryAndRoundTrips()
    {
        var options = new DispatcherOptions("gateway");
        var outbound = new RecordingUnaryOutbound();
        options.AddUnaryOutbound("remote", null, outbound);
        var dispatcher = new YARPCore.Dispatcher.Dispatcher(options);

        var client = dispatcher.CreateJsonClient<JsonEchoRequest, JsonEchoResponse>(
            "remote",
            "echo::greet",
            codec => codec.Encoding = "application/json");

        var meta = new RequestMeta(service: "remote", procedure: "echo::greet", transport: "test");
        var request = new Request<JsonEchoRequest>(meta, new JsonEchoRequest("sally"));
        var callResult = await client.CallAsync(request, CancellationToken.None);

        Assert.True(callResult.IsSuccess);
        Assert.Equal("hello sally", callResult.Value.Body.Message);

        Assert.Single(outbound.Requests);
        var recorded = outbound.Requests[0];
        Assert.Equal("application/json", recorded.Meta.Encoding);

        using var document = JsonDocument.Parse(recorded.Body.ToArray());
        Assert.Equal("sally", document.RootElement.GetProperty("name").GetString());

        Assert.True(dispatcher.Codecs.TryResolve<JsonEchoRequest, JsonEchoResponse>(
            ProcedureCodecScope.Outbound,
            "remote",
            "echo::greet",
            ProcedureKind.Unary,
            out var outboundCodec));
        Assert.Equal("application/json", outboundCodec.Encoding);
    }

    private sealed record JsonEchoRequest(string Name);

    private sealed record JsonEchoResponse(string Message);

    private sealed class RecordingUnaryOutbound : IUnaryOutbound
    {
        public List<Request<ReadOnlyMemory<byte>>> Requests { get; } = [];

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new Request<ReadOnlyMemory<byte>>(request.Meta, request.Body));

            using var document = JsonDocument.Parse(request.Body.ToArray());
            var name = document.RootElement.GetProperty("name").GetString() ?? string.Empty;
            var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new JsonEchoResponse($"hello {name}"));
            var responseMeta = new ResponseMeta(encoding: request.Meta.Encoding, transport: request.Meta.Transport);
            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(responsePayload, responseMeta)));
        }
    }
}
