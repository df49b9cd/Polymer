using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using NSubstitute;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherShadowingExtensionsTests
{
    [Fact]
    public void AddTeeUnaryOutbound_RegistersTeeOutbound()
    {
        var options = new DispatcherOptions("svc");
        options.AddTeeUnaryOutbound("downstream", null, Substitute.For<IUnaryOutbound>(), Substitute.For<IUnaryOutbound>());

        var dispatcher = new Dispatcher(options);
        var outbound = dispatcher.ClientConfig("downstream").ResolveUnary();

        Assert.IsType<TeeUnaryOutbound>(outbound);
    }

    [Fact]
    public void AddTeeOnewayOutbound_RegistersTeeOutbound()
    {
        var options = new DispatcherOptions("svc");
        options.AddTeeOnewayOutbound("downstream", "shadow", Substitute.For<IOnewayOutbound>(), Substitute.For<IOnewayOutbound>());

        var dispatcher = new Dispatcher(options);
        var outbound = dispatcher.ClientConfig("downstream").ResolveOneway("shadow");

        Assert.IsType<TeeOnewayOutbound>(outbound);
    }
}
