using NSubstitute;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class FewestPendingPeerChooserTests
{
    private static RequestMeta Meta() => new RequestMeta(service: "svc", transport: "http");

    [Fact(Timeout = TestTimeouts.Default)]
    public void RequiresAtLeastOnePeer()
    {
        Should.Throw<ArgumentException>(() => new FewestPendingPeerChooser([]));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ChoosesPeerWithLowestInflight()
    {
        var high = Substitute.For<IPeer>(); high.Identifier.Returns("high"); high.Status.Returns(new PeerStatus(PeerState.Available, 10, null, null)); high.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var low = Substitute.For<IPeer>(); low.Identifier.Returns("low"); low.Status.Returns(new PeerStatus(PeerState.Available, 1, null, null)); low.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var chooser = new FewestPendingPeerChooser(high, low);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();
        res.Value.Peer.ShouldBeSameAs(low);
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask WhenTie_PicksOneOfBest()
    {
        var a = Substitute.For<IPeer>(); a.Identifier.Returns("a"); a.Status.Returns(new PeerStatus(PeerState.Available, 2, null, null)); a.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var b = Substitute.For<IPeer>(); b.Identifier.Returns("b"); b.Status.Returns(new PeerStatus(PeerState.Available, 2, null, null)); b.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var c = Substitute.For<IPeer>(); c.Identifier.Returns("c"); c.Status.Returns(new PeerStatus(PeerState.Available, 5, null, null)); c.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var chooser = new FewestPendingPeerChooser(a, b, c);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();
        new[] { "a", "b" }.ShouldContain(res.Value.Peer.Identifier);
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AllUnavailable_ReturnsExhausted()
    {
        var ua = Substitute.For<IPeer>(); ua.Identifier.Returns("ua"); ua.Status.Returns(new PeerStatus(PeerState.Unavailable, 0, null, null));
        var ub = Substitute.For<IPeer>(); ub.Identifier.Returns("ub"); ub.Status.Returns(new PeerStatus(PeerState.Unavailable, 0, null, null));
        var chooser = new FewestPendingPeerChooser(ua, ub);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(res.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask SelectedPeerRejects_ReturnsExhausted()
    {
        var p = Substitute.For<IPeer>(); p.Identifier.Returns("p"); p.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null)); p.TryAcquire(Arg.Any<CancellationToken>()).Returns(false);
        var chooser = new FewestPendingPeerChooser(p);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(res.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);
    }
}
