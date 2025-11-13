using System.Text.Json;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherJsonExtensionsTests
{
    [Fact]
    public async Task RegisterJsonUnary_RegistersProcedureAndCodec()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        dispatcher.RegisterJsonUnary<JsonDocument, JsonDocument>(
            "echo",
            async (context, request) =>
            {
                await Task.CompletedTask;
                return Response<JsonDocument>.Create(request, new ResponseMeta(encoding: "custom/json"));
            },
            configureCodec: builder => builder.Encoding = "custom/json",
            configureProcedure: builder => builder.AddAlias("alias"));

        Assert.True(dispatcher.TryGetProcedure("echo", ProcedureKind.Unary, out var spec));
        var unary = Assert.IsType<UnaryProcedureSpec>(spec);
        Assert.Contains("alias", unary.Aliases);

        Assert.True(dispatcher.Codecs.TryResolve<JsonDocument, JsonDocument>(ProcedureCodecScope.Inbound, "svc", "echo", ProcedureKind.Unary, out var codec));
        Assert.Equal("custom/json", codec.Encoding);
    }

    [Fact]
    public void CreateJsonClient_RegistersOutboundCodecWhenMissing()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("downstream", null, Substitute.For<IUnaryOutbound>());
        var dispatcher = new Dispatcher(options);

        var client = dispatcher.CreateJsonClient<JsonDocument, JsonDocument>(
            "downstream",
            "echo",
            aliases: ["alias"]);

        Assert.IsType<Core.Clients.UnaryClient<JsonDocument, JsonDocument>>(client);
        Assert.True(dispatcher.Codecs.TryResolve<JsonDocument, JsonDocument>(ProcedureCodecScope.Outbound, "downstream", "echo", ProcedureKind.Unary, out _));
        Assert.True(dispatcher.Codecs.TryResolve<JsonDocument, JsonDocument>(ProcedureCodecScope.Outbound, "downstream", "alias", ProcedureKind.Unary, out _));
    }

    [Fact]
    public void CreateJsonClient_ReusesExistingCodec()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("downstream", null, Substitute.For<IUnaryOutbound>());
        var dispatcher = new Dispatcher(options);
        var codec = new TestHelpers.TestCodec<JsonDocument, JsonDocument>();
        dispatcher.Codecs.RegisterOutbound("downstream", "echo", ProcedureKind.Unary, codec);

        var client = dispatcher.CreateJsonClient<JsonDocument, JsonDocument>("downstream", "echo");

        Assert.IsType<Core.Clients.UnaryClient<JsonDocument, JsonDocument>>(client);
    }

    [Fact]
    public void CreateJsonClient_WithCustomCodecRegistration_IsIdempotent()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("downstream", null, Substitute.For<IUnaryOutbound>());
        var dispatcher = new Dispatcher(options);
        var configureInvocations = 0;
        Action<JsonCodecBuilder<JsonDocument, JsonDocument>> configure = builder =>
        {
            configureInvocations++;
            builder.Encoding = "application/json";
        };

        var first = dispatcher.CreateJsonClient<JsonDocument, JsonDocument>("downstream", "echo", configure);
        var second = dispatcher.CreateJsonClient<JsonDocument, JsonDocument>("downstream", "echo", configure);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, configureInvocations);
    }
}
