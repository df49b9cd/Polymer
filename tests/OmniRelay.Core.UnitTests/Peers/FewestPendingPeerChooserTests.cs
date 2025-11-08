using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using NSubstitute;
using OmniRelay.Core;
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
        Assert.Throws<ArgumentException>(() => new FewestPendingPeerChooser(Array.Empty<IPeer>()));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ChoosesPeerWithLowestInflight()
    {
        var high = Substitute.For<IPeer>(); high.Identifier.Returns("high"); high.Status.Returns(new PeerStatus(PeerState.Available, 10, null, null)); high.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var low = Substitute.For<IPeer>(); low.Identifier.Returns("low"); low.Status.Returns(new PeerStatus(PeerState.Available, 1, null, null)); low.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var chooser = new FewestPendingPeerChooser(high, low);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        Assert.True(res.IsSuccess);
        Assert.Same(low, res.Value.Peer);
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task WhenTie_PicksOneOfBest()
    {
        var a = Substitute.For<IPeer>(); a.Identifier.Returns("a"); a.Status.Returns(new PeerStatus(PeerState.Available, 2, null, null)); a.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var b = Substitute.For<IPeer>(); b.Identifier.Returns("b"); b.Status.Returns(new PeerStatus(PeerState.Available, 2, null, null)); b.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var c = Substitute.For<IPeer>(); c.Identifier.Returns("c"); c.Status.Returns(new PeerStatus(PeerState.Available, 5, null, null)); c.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var chooser = new FewestPendingPeerChooser(a, b, c);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        Assert.True(res.IsSuccess);
        Assert.Contains(res.Value.Peer.Identifier, new[] { "a", "b" });
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AllUnavailable_ReturnsExhausted()
    {
        var ua = Substitute.For<IPeer>(); ua.Identifier.Returns("ua"); ua.Status.Returns(new PeerStatus(PeerState.Unavailable, 0, null, null));
        var ub = Substitute.For<IPeer>(); ub.Identifier.Returns("ub"); ub.Status.Returns(new PeerStatus(PeerState.Unavailable, 0, null, null));
        var chooser = new FewestPendingPeerChooser(ua, ub);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        Assert.True(res.IsFailure);
        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(res.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SelectedPeerRejects_ReturnsExhausted()
    {
        var p = Substitute.For<IPeer>(); p.Identifier.Returns("p"); p.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null)); p.TryAcquire(Arg.Any<CancellationToken>()).Returns(false);
        var chooser = new FewestPendingPeerChooser(p);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        Assert.True(res.IsFailure);
        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(res.Error!));
    }
}
