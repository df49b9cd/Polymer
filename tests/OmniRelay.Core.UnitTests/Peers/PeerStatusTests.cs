using System;
using OmniRelay.Core.Peers;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class PeerStatusTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Unknown_HasExpectedDefaults()
    {
        var s = PeerStatus.Unknown;
        Assert.Equal(PeerState.Unknown, s.State);
        Assert.Equal(0, s.Inflight);
        Assert.Null(s.LastFailure);
        Assert.Null(s.LastSuccess);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Ctor_Sets_Properties()
    {
        var now = DateTimeOffset.UtcNow;
        var s = new PeerStatus(PeerState.Available, 3, now, now.AddMinutes(-1));
        Assert.Equal(PeerState.Available, s.State);
        Assert.Equal(3, s.Inflight);
        Assert.Equal(now, s.LastSuccess);
        Assert.Equal(now.AddMinutes(-1), s.LastFailure);
    }
}
