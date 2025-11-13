using System.Text.Json;
using Hugo;
using Json.Schema;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Dispatcher;

public class DispatcherJsonExtensionsTests
{
    [Fact]
    public async Task RegisterJsonUnary_RegistersCodecAndHandlesRequest()
    {
        var options = new DispatcherOptions("echo");
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

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
                codec.SerializerContext = OmniRelayTestsJsonContext.Default;
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

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new JsonEchoRequest("sandy"),
            OmniRelayTestsJsonContext.Default.JsonEchoRequest);
        var requestMeta = new RequestMeta(service: "echo", procedure: "echo::greet", transport: "test");
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload);

        var result = await dispatcher.InvokeUnaryAsync("echo::greet", request, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.ToString() ?? "unknown error");

        var decoded = JsonSerializer.Deserialize(
            result.Value.Body.Span,
            OmniRelayTestsJsonContext.Default.JsonEchoResponse);
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
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

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

    [Fact]
    public async Task RegisterJsonUnary_HandlerExceptionReturnsError()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("svc"));

        ValueTask<Response<JsonEchoResponse>> Handler(JsonUnaryContext _, JsonEchoRequest __) =>
            throw new InvalidOperationException("boom");

        dispatcher.RegisterJsonUnary<JsonEchoRequest, JsonEchoResponse>("svc::fail", Handler);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new JsonEchoRequest("x"),
            OmniRelayTestsJsonContext.Default.JsonEchoRequest);
        var meta = new RequestMeta(service: "svc", procedure: "svc::fail", transport: "test");
        var request = new Request<ReadOnlyMemory<byte>>(meta, payload);

        var result = await dispatcher.InvokeUnaryAsync("svc::fail", request, TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public async Task CreateJsonClient_ReusesExistingCodecWhenUnconfigured()
    {
        var options = new DispatcherOptions("svc");
        var outbound = new RecordingUnaryOutbound();
        options.AddUnaryOutbound("remote", null, outbound);
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var codec = new JsonCodec<JsonEchoRequest, JsonEchoResponse>(
            serializerContext: OmniRelayTestsJsonContext.Default,
            encoding: "application/json");
        dispatcher.Codecs.RegisterOutbound("remote", "svc::op", ProcedureKind.Unary, codec);

        var client = dispatcher.CreateJsonClient<JsonEchoRequest, JsonEchoResponse>(
            "remote",
            "svc::op",
            configureCodec: null);

        var meta = new RequestMeta(service: "remote", procedure: "svc::op", transport: "test");
        var response = await client.CallAsync(new Request<JsonEchoRequest>(meta, new JsonEchoRequest("lane")), TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccess);

        Assert.True(dispatcher.Codecs.TryResolve<JsonEchoRequest, JsonEchoResponse>(
            ProcedureCodecScope.Outbound,
            "remote",
            "svc::op",
            ProcedureKind.Unary,
            out var resolved));
        Assert.Same(codec, resolved);
    }

    [Fact]
    public void CreateJsonClient_RegistersCodecWithAliases()
    {
        var options = new DispatcherOptions("svc");
        var outbound = new RecordingUnaryOutbound();
        options.AddUnaryOutbound("remote", null, outbound);
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var client = dispatcher.CreateJsonClient<JsonEchoRequest, JsonEchoResponse>(
            "remote",
            "svc::register",
            codec =>
            {
                codec.Encoding = "application/json";
                codec.SerializerContext = OmniRelayTestsJsonContext.Default;
            },
            outboundKey: null,
            aliases: ["svc::alias"]);

        Assert.NotNull(client);

        Assert.True(dispatcher.Codecs.TryResolve<JsonEchoRequest, JsonEchoResponse>(
            ProcedureCodecScope.Outbound,
            "remote",
            "svc::alias",
            ProcedureKind.Unary,
            out _));
    }

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

            var payload = JsonSerializer.Deserialize(
                request.Body.Span,
                OmniRelayTestsJsonContext.Default.JsonEchoRequest);
            var name = payload?.Name ?? string.Empty;
            var responsePayload = JsonSerializer.SerializeToUtf8Bytes(
                new JsonEchoResponse($"hello {name}"),
                OmniRelayTestsJsonContext.Default.JsonEchoResponse);
            var responseMeta = new ResponseMeta(encoding: request.Meta.Encoding, transport: request.Meta.Transport);
            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(responsePayload, responseMeta)));
        }
    }
}

internal sealed record JsonEchoRequest(string Name)
{
    public string Name { get; init; } = Name;
}

internal sealed record JsonEchoResponse(string Message)
{
    public string Message { get; init; } = Message;
}
