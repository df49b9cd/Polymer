using OmniRelay.Core.Gossip;
using Xunit;

namespace OmniRelay.Core.UnitTests.Gossip;

public sealed class MeshGossipPeerEndpointTests
{
    [Theory]
    [InlineData("localhost:17421", "localhost", 17421)]
    [InlineData("127.0.0.1:8080", "127.0.0.1", 8080)]
    [InlineData("peer.example.com:443", "peer.example.com", 443)]
    [InlineData("  peer:9000  ", "peer", 9000)]
    public void TryParse_ValidHostPort_ReturnsTrue(string input, string expectedHost, int expectedPort)
    {
        var result = MeshGossipPeerEndpoint.TryParse(input, out var endpoint);

        Assert.True(result);
        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(expectedPort, endpoint.Port);
    }

    [Theory]
    [InlineData("https://localhost:17421", "localhost", 17421)]
    [InlineData("https://peer.example.com:8080/path", "peer.example.com", 8080)]
    public void TryParse_ValidUri_ReturnsTrue(string input, string expectedHost, int expectedPort)
    {
        var result = MeshGossipPeerEndpoint.TryParse(input, out var endpoint);

        Assert.True(result);
        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(expectedPort, endpoint.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noport")]
    [InlineData("host:")]
    [InlineData(":8080")]
    [InlineData("host:-8080")]
    [InlineData("host:abc")]
    [InlineData("https://localhost")]
    [InlineData("https://localhost:0")]
    public void TryParse_InvalidInput_ReturnsFalse(string input)
    {
        var result = MeshGossipPeerEndpoint.TryParse(input, out var endpoint);

        Assert.False(result);
        Assert.Equal(default(MeshGossipPeerEndpoint), endpoint);
    }

    [Fact]
    public void BuildRequestUri_CreatesCorrectUri()
    {
        var endpoint = new MeshGossipPeerEndpoint("localhost", 17421);
        var uri = endpoint.BuildRequestUri();

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("localhost", uri.Host);
        Assert.Equal(17421, uri.Port);
        Assert.Equal("/mesh/gossip/v1/messages", uri.AbsolutePath);
    }

    [Fact]
    public void ToString_ReturnsHostColonPort()
    {
        var endpoint = new MeshGossipPeerEndpoint("peer.example.com", 8080);
        var result = endpoint.ToString();

        Assert.Equal("peer.example.com:8080", result);
    }

    [Fact]
    public void Equality_ComparesHostAndPort()
    {
        var endpoint1 = new MeshGossipPeerEndpoint("localhost", 17421);
        var endpoint2 = new MeshGossipPeerEndpoint("localhost", 17421);
        var endpoint3 = new MeshGossipPeerEndpoint("localhost", 8080);
        var endpoint4 = new MeshGossipPeerEndpoint("other", 17421);

        Assert.Equal(endpoint1, endpoint2);
        Assert.NotEqual(endpoint1, endpoint3);
        Assert.NotEqual(endpoint1, endpoint4);
    }
}
