using Hugo;
using NSubstitute;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherClientExtensionsTests
{
    [Fact]
    public void CreateUnaryClient_WithCodec_ResolvesOutbound()
    {
        var dispatcher = CreateDispatcher(out var unaryOutbound);
        var codec = new TestHelpers.TestCodec<string, string>();

        var client = dispatcher.CreateUnaryClient("downstream", codec);

        Assert.IsType<UnaryClient<string, string>>(client);
        Assert.False(unaryOutbound.ReceivedCalls().Any());
    }

    [Fact]
    public void CreateUnaryClient_WithRegisteredCodec_UsesRegistry()
    {
        var dispatcher = CreateDispatcher(out _);
        var codec = new TestHelpers.TestCodec<string, string>();
        dispatcher.Codecs.RegisterOutbound("downstream", "echo", ProcedureKind.Unary, codec);

        var client = dispatcher.CreateUnaryClient<string, string>("downstream", "echo");

        Assert.IsType<UnaryClient<string, string>>(client);
    }

    [Fact]
    public void CreateDuplexClient_WhenOutboundMissing_Throws()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        Assert.Throws<ResultException>(() =>
            dispatcher.CreateDuplexStreamClient<string, string>("remote", new TestHelpers.TestCodec<string, string>()));
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
