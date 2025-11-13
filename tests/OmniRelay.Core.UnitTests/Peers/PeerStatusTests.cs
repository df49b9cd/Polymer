using OmniRelay.Core.Peers;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class PeerStatusTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Unknown_HasExpectedDefaults()
    {
        var s = PeerStatus.Unknown;
        s.State.ShouldBe(PeerState.Unknown);
        s.Inflight.ShouldBe(0);
        s.LastFailure.ShouldBeNull();
        s.LastSuccess.ShouldBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Ctor_Sets_Properties()
    {
        var now = DateTimeOffset.UtcNow;
        var s = new PeerStatus(PeerState.Available, 3, now, now.AddMinutes(-1));
        s.State.ShouldBe(PeerState.Available);
        s.Inflight.ShouldBe(3);
        s.LastSuccess.ShouldBe(now);
        s.LastFailure.ShouldBe(now.AddMinutes(-1));
    }
}
