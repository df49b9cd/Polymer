using AwesomeAssertions;
using Hugo;
using NSubstitute;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherClientExtensionsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateUnaryClient_WithCodec_ResolvesOutbound()
    {
        var dispatcher = CreateDispatcher(out var unaryOutbound);
        var codec = new TestHelpers.TestCodec<string, string>();

        var clientResult = dispatcher.CreateUnaryClient("downstream", codec);

        clientResult.IsSuccess.Should().BeTrue();
        var client = clientResult.Value;

        client.Should().BeOfType<UnaryClient<string, string>>();
        unaryOutbound.ReceivedCalls().Should().BeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateUnaryClient_WithRegisteredCodec_UsesRegistry()
    {
        var dispatcher = CreateDispatcher(out _);
        var codec = new TestHelpers.TestCodec<string, string>();
        dispatcher.Codecs.RegisterOutbound("downstream", "echo", ProcedureKind.Unary, codec);

        var clientResult = dispatcher.CreateUnaryClient<string, string>("downstream", "echo");

        clientResult.IsSuccess.Should().BeTrue();
        var client = clientResult.Value;

        client.Should().BeOfType<UnaryClient<string, string>>();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateDuplexClient_WhenOutboundMissing_ReturnsFailure()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        var result = dispatcher.CreateDuplexStreamClient<string, string>("remote", new TestHelpers.TestCodec<string, string>());

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.NotFound);
    }

    private static Dispatcher CreateDispatcher(out IUnaryOutbound unaryOutbound)
    {
        var options = new DispatcherOptions("svc");
        unaryOutbound = Substitute.For<IUnaryOutbound>();
        options.AddUnaryOutbound("downstream", null, unaryOutbound);
        options.AddOnewayOutbound("downstream", null, Substitute.For<IOnewayOutbound>());
        options.AddStreamOutbound("downstream", null, Substitute.For<IStreamOutbound>());
        options.AddClientStreamOutbound("downstream", null, Substitute.For<IClientStreamOutbound>());
        options.AddDuplexOutbound("downstream", null, Substitute.For<IDuplexOutbound>());
        return new Dispatcher(options);
    }
}
