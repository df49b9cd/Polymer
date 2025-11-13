using System.Collections.Immutable;
using NSubstitute;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class ClientConfigurationTests
{
    [Fact]
    public void Service_ReturnsOutboundService()
    {
        var config = CreateConfiguration(out var unaryOutbound);

        Assert.Equal("downstream", config.Service);
        Assert.Same(unaryOutbound, config.ResolveUnary());
    }

    [Fact]
    public void Resolve_WithUnknownKey_ReturnsNull()
    {
        var config = CreateConfiguration(out _);

        Assert.Null(config.ResolveOneway("missing"));
        Assert.Null(config.ResolveStream("missing"));
        Assert.Null(config.ResolveClientStream("missing"));
        Assert.Null(config.ResolveDuplex("missing"));
    }

    [Fact]
    public void TryGet_ReturnsFalseWhenOutboundNotFound()
    {
        var config = CreateConfiguration(out _);

        Assert.False(config.TryGetUnary("missing", out _));
        Assert.False(config.TryGetOneway("missing", out _));
        Assert.False(config.TryGetStream("missing", out _));
        Assert.False(config.TryGetClientStream("missing", out _));
        Assert.False(config.TryGetDuplex("missing", out _));
    }

    [Fact]
    public void Middleware_CollectionsExposeConfiguredInstances()
    {
        var unaryMiddleware = ImmutableArray.Create(Substitute.For<IUnaryOutboundMiddleware>());
        var onewayMiddleware = ImmutableArray.Create(Substitute.For<IOnewayOutboundMiddleware>());
        var streamMiddleware = ImmutableArray.Create(Substitute.For<IStreamOutboundMiddleware>());
        var clientStreamMiddleware = ImmutableArray.Create(Substitute.For<IClientStreamOutboundMiddleware>());
        var duplexMiddleware = ImmutableArray.Create(Substitute.For<IDuplexOutboundMiddleware>());

        var collection = new OutboundRegistry(
            "downstream",
            [],
            [],
            [],
            [],
            []);

        var config = new ClientConfiguration(
            collection,
            unaryMiddleware,
            onewayMiddleware,
            streamMiddleware,
            clientStreamMiddleware,
            duplexMiddleware);

        Assert.Same(unaryMiddleware[0], config.UnaryMiddleware[0]);
        Assert.Same(onewayMiddleware[0], config.OnewayMiddleware[0]);
        Assert.Same(streamMiddleware[0], config.StreamMiddleware[0]);
        Assert.Same(clientStreamMiddleware[0], config.ClientStreamMiddleware[0]);
        Assert.Same(duplexMiddleware[0], config.DuplexMiddleware[0]);
    }

    private static ClientConfiguration CreateConfiguration(out IUnaryOutbound unaryOutbound)
    {
        unaryOutbound = Substitute.For<IUnaryOutbound>();
        var onewayOutbound = Substitute.For<IOnewayOutbound>();
        var streamOutbound = Substitute.For<IStreamOutbound>();
        var clientStreamOutbound = Substitute.For<IClientStreamOutbound>();
        var duplexOutbound = Substitute.For<IDuplexOutbound>();

        var collection = new OutboundRegistry(
            "downstream",
            ImmutableDictionary<string, IUnaryOutbound>.Empty.Add(OutboundRegistry.DefaultKey, unaryOutbound),
            ImmutableDictionary<string, IOnewayOutbound>.Empty.Add(OutboundRegistry.DefaultKey, onewayOutbound),
            ImmutableDictionary<string, IStreamOutbound>.Empty.Add(OutboundRegistry.DefaultKey, streamOutbound),
            ImmutableDictionary<string, IClientStreamOutbound>.Empty.Add(OutboundRegistry.DefaultKey, clientStreamOutbound),
            ImmutableDictionary<string, IDuplexOutbound>.Empty.Add(OutboundRegistry.DefaultKey, duplexOutbound));

        return new ClientConfiguration(
            collection,
            [],
            [],
            [],
            [],
            []);
    }
}
