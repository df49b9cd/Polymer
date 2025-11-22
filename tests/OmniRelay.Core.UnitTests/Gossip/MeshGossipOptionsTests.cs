using OmniRelay.Core.Gossip;
using Xunit;

namespace OmniRelay.Core.UnitTests.Gossip;

public sealed class MeshGossipOptionsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void DefaultOptions_HasExpectedValues()
    {
        var options = new MeshGossipOptions();

        options.Enabled.ShouldBeTrue();
        options.NodeId.ShouldStartWith("mesh-");
        options.Role.ShouldBe("worker");
        options.ClusterId.ShouldBe("local");
        options.Region.ShouldBe("local");
        options.MeshVersion.ShouldBe("dev");
        options.Http3Support.ShouldBeTrue();
        options.BindAddress.ShouldBe("0.0.0.0");
        options.Port.ShouldBe(17421);
        options.Interval.ShouldBe(TimeSpan.FromSeconds(1));
        options.Fanout.ShouldBe(3);
        options.SuspicionInterval.ShouldBe(TimeSpan.FromSeconds(5));
        options.PingTimeout.ShouldBe(TimeSpan.FromSeconds(2));
        options.RetransmitLimit.ShouldBe(3);
        options.MetadataRefreshPeriod.ShouldBe(TimeSpan.FromSeconds(30));
        options.CertificateReloadInterval.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CurrentSchemaVersion_IsV1()
    {
        MeshGossipOptions.CurrentSchemaVersion.ShouldBe("v1");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void GetNormalizedSeedPeers_ReturnsEmptyList_WhenNoSeeds()
    {
        var options = new MeshGossipOptions();
        var normalized = options.GetNormalizedSeedPeers();

        normalized.ShouldBeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void GetNormalizedSeedPeers_TrimsWhitespace()
    {
        var options = new MeshGossipOptions();
        options.SeedPeers.Add("  peer1:17421  ");
        options.SeedPeers.Add("peer2:17421");
        options.SeedPeers.Add("   ");

        var normalized = options.GetNormalizedSeedPeers();

        normalized.Count.ShouldBe(2);
        normalized[0].ShouldBe("peer1:17421");
        normalized[1].ShouldBe("peer2:17421");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void GetNormalizedSeedPeers_FiltersEmptyEntries()
    {
        var options = new MeshGossipOptions();
        options.SeedPeers.Add("");
        options.SeedPeers.Add("   ");
        options.SeedPeers.Add("valid:17421");
        options.SeedPeers.Add(null!);

        var normalized = options.GetNormalizedSeedPeers();

        normalized.ShouldHaveSingleItem();
        normalized[0].ShouldBe("valid:17421");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Labels_CanBeModified()
    {
        var options = new MeshGossipOptions();
        options.Labels["key1"] = "value1";
        options.Labels["key2"] = "value2";

        options.Labels.Count.ShouldBe(2);
        options.Labels["key1"].ShouldBe("value1");
        options.Labels["key2"].ShouldBe("value2");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void AdvertisePort_DefaultsToNull()
    {
        var options = new MeshGossipOptions();
        options.AdvertisePort.ShouldBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TlsOptions_DefaultValues()
    {
        var tlsOptions = new MeshGossipTlsOptions();

        tlsOptions.CheckCertificateRevocation.ShouldBeTrue();
        tlsOptions.AllowedThumbprints.ShouldBeEmpty();
        tlsOptions.ReloadIntervalOverride.ShouldBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TlsOptions_AllowedThumbprints_CanBeModified()
    {
        var tlsOptions = new MeshGossipTlsOptions();
        tlsOptions.AllowedThumbprints.Add("ABC123");
        tlsOptions.AllowedThumbprints.Add("DEF456");

        tlsOptions.AllowedThumbprints.Count.ShouldBe(2);
        tlsOptions.AllowedThumbprints.ShouldContain("ABC123");
        tlsOptions.AllowedThumbprints.ShouldContain("DEF456");
    }
}
