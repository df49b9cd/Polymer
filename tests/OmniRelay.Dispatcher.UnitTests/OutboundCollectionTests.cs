using System;
using System.Collections.Immutable;
using NSubstitute;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class OutboundRegistryTests
{
    [Fact]
    public void Resolve_WithNullKey_ReturnsDefaultBinding()
    {
        var unary = Substitute.For<IUnaryOutbound>();
        var collection = CreateCollection(unaryOutbound: unary);

        Assert.Same(unary, collection.ResolveUnary());
        Assert.Same(unary, collection.ResolveUnary(" "));
    }

    [Fact]
    public void Resolve_WithAlternateKey_IsCaseInsensitive()
    {
        var unary = Substitute.For<IUnaryOutbound>();
        var map = ImmutableDictionary.Create<string, IUnaryOutbound>(StringComparer.OrdinalIgnoreCase)
            .Add(OutboundRegistry.DefaultKey, Substitute.For<IUnaryOutbound>())
            .Add("primary", unary);

        var collection = new OutboundRegistry(
            "downstream",
            map,
            [],
            [],
            [],
            []);

        Assert.Same(unary, collection.ResolveUnary("PRIMARY"));
    }

    [Fact]
    public void TryGet_ReturnsFalseWhenKeyMissing()
    {
        var collection = CreateCollection();

        Assert.False(collection.TryGetUnary("missing", out _));
        Assert.False(collection.TryGetOneway("missing", out _));
        Assert.False(collection.TryGetStream("missing", out _));
        Assert.False(collection.TryGetClientStream("missing", out _));
        Assert.False(collection.TryGetDuplex("missing", out _));
    }

    private static OutboundRegistry CreateCollection(
        IUnaryOutbound? unaryOutbound = null)
    {
        unaryOutbound ??= Substitute.For<IUnaryOutbound>();

        return new OutboundRegistry(
            "downstream",
            ImmutableDictionary<string, IUnaryOutbound>.Empty.Add(OutboundRegistry.DefaultKey, unaryOutbound),
            ImmutableDictionary<string, IOnewayOutbound>.Empty.Add(OutboundRegistry.DefaultKey, Substitute.For<IOnewayOutbound>()),
            ImmutableDictionary<string, IStreamOutbound>.Empty.Add(OutboundRegistry.DefaultKey, Substitute.For<IStreamOutbound>()),
            ImmutableDictionary<string, IClientStreamOutbound>.Empty.Add(OutboundRegistry.DefaultKey, Substitute.For<IClientStreamOutbound>()),
            ImmutableDictionary<string, IDuplexOutbound>.Empty.Add(OutboundRegistry.DefaultKey, Substitute.For<IDuplexOutbound>()));
    }
}
