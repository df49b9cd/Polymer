using System.Text.Json;
using AwesomeAssertions;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherJsonExtensionsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RegisterJsonUnary_RegistersProcedureAndCodec()
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

        dispatcher.TryGetProcedure("echo", ProcedureKind.Unary, out var spec).Should().BeTrue();
        var unary = spec.Should().BeOfType<UnaryProcedureSpec>().Which;
        unary.Aliases.Should().Contain("alias");

        dispatcher.Codecs.TryResolve<JsonDocument, JsonDocument>(ProcedureCodecScope.Inbound, "svc", "echo", ProcedureKind.Unary, out var codec)
            .Should().BeTrue();
        codec.Encoding.Should().Be("custom/json");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateJsonClient_RegistersOutboundCodecWhenMissing()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("downstream", null, Substitute.For<IUnaryOutbound>());
        var dispatcher = new Dispatcher(options);

        var clientResult = dispatcher.CreateJsonClient<JsonDocument, JsonDocument>(
            "downstream",
            "echo",
            aliases: ["alias"]);

        clientResult.IsSuccess.Should().BeTrue(clientResult.Error?.Message);
        var client = clientResult.Value;

        client.Should().BeOfType<Core.Clients.UnaryClient<JsonDocument, JsonDocument>>();
        dispatcher.Codecs.TryResolve<JsonDocument, JsonDocument>(ProcedureCodecScope.Outbound, "downstream", "echo", ProcedureKind.Unary, out _)
            .Should().BeTrue();
        dispatcher.Codecs.TryResolve<JsonDocument, JsonDocument>(ProcedureCodecScope.Outbound, "downstream", "alias", ProcedureKind.Unary, out _)
            .Should().BeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateJsonClient_ReusesExistingCodec()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("downstream", null, Substitute.For<IUnaryOutbound>());
        var dispatcher = new Dispatcher(options);
        var codec = new TestHelpers.TestCodec<JsonDocument, JsonDocument>();
        dispatcher.Codecs.RegisterOutbound("downstream", "echo", ProcedureKind.Unary, codec);

        var clientResult = dispatcher.CreateJsonClient<JsonDocument, JsonDocument>("downstream", "echo");

        clientResult.IsSuccess.Should().BeTrue(clientResult.Error?.Message);
        var client = clientResult.Value;

        client.Should().BeOfType<Core.Clients.UnaryClient<JsonDocument, JsonDocument>>();
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

        first.IsSuccess.Should().BeTrue(first.Error?.Message);
        second.IsSuccess.Should().BeTrue(second.Error?.Message);

        first.Value.Should().NotBeNull();
        second.Value.Should().NotBeNull();
        configureInvocations.Should().Be(1);
    }
}
