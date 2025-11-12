using OmniRelay.Core.Gossip;
using Xunit;

namespace OmniRelay.Core.UnitTests.Gossip;

public sealed class MeshGossipOptionsTests
{
    [Fact]
    public void DefaultOptions_HasExpectedValues()
    {
        var options = new MeshGossipOptions();

        Assert.True(options.Enabled);
        Assert.StartsWith("mesh-", options.NodeId);
        Assert.Equal("worker", options.Role);
        Assert.Equal("local", options.ClusterId);
        Assert.Equal("local", options.Region);
        Assert.Equal("dev", options.MeshVersion);
        Assert.True(options.Http3Support);
        Assert.Equal("0.0.0.0", options.BindAddress);
        Assert.Equal(17421, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Interval);
        Assert.Equal(3, options.Fanout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.SuspicionInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PingTimeout);
        Assert.Equal(3, options.RetransmitLimit);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MetadataRefreshPeriod);
        Assert.Equal(TimeSpan.FromMinutes(5), options.CertificateReloadInterval);
    }

    [Fact]
    public void CurrentSchemaVersion_IsV1()
    {
        Assert.Equal("v1", MeshGossipOptions.CurrentSchemaVersion);
    }

    [Fact]
    public void GetNormalizedSeedPeers_ReturnsEmptyList_WhenNoSeeds()
    {
        var options = new MeshGossipOptions();
        var normalized = options.GetNormalizedSeedPeers();

        Assert.Empty(normalized);
    }

    [Fact]
    public void GetNormalizedSeedPeers_TrimsWhitespace()
    {
        var options = new MeshGossipOptions();
        options.SeedPeers.Add("  peer1:17421  ");
        options.SeedPeers.Add("peer2:17421");
        options.SeedPeers.Add("   ");

        var normalized = options.GetNormalizedSeedPeers();

        Assert.Equal(2, normalized.Count);
        Assert.Equal("peer1:17421", normalized[0]);
        Assert.Equal("peer2:17421", normalized[1]);
    }

    [Fact]
    public void GetNormalizedSeedPeers_FiltersEmptyEntries()
    {
        var options = new MeshGossipOptions();
        options.SeedPeers.Add("");
        options.SeedPeers.Add("   ");
        options.SeedPeers.Add("valid:17421");
        options.SeedPeers.Add(null!);

        var normalized = options.GetNormalizedSeedPeers();

        Assert.Single(normalized);
        Assert.Equal("valid:17421", normalized[0]);
    }

    [Fact]
    public void Labels_CanBeModified()
    {
        var options = new MeshGossipOptions();
        options.Labels["key1"] = "value1";
        options.Labels["key2"] = "value2";

        Assert.Equal(2, options.Labels.Count);
        Assert.Equal("value1", options.Labels["key1"]);
        Assert.Equal("value2", options.Labels["key2"]);
    }

    [Fact]
    public void AdvertisePort_DefaultsToNull()
    {
        var options = new MeshGossipOptions();
        Assert.Null(options.AdvertisePort);
    }

    [Fact]
    public void TlsOptions_DefaultValues()
    {
        var tlsOptions = new MeshGossipTlsOptions();

        Assert.True(tlsOptions.CheckCertificateRevocation);
        Assert.Empty(tlsOptions.AllowedThumbprints);
        Assert.Null(tlsOptions.ReloadIntervalOverride);
    }

    [Fact]
    public void TlsOptions_AllowedThumbprints_CanBeModified()
    {
        var tlsOptions = new MeshGossipTlsOptions();
        tlsOptions.AllowedThumbprints.Add("ABC123");
        tlsOptions.AllowedThumbprints.Add("DEF456");

        Assert.Equal(2, tlsOptions.AllowedThumbprints.Count);
        Assert.Contains("ABC123", tlsOptions.AllowedThumbprints);
        Assert.Contains("DEF456", tlsOptions.AllowedThumbprints);
    }
}
