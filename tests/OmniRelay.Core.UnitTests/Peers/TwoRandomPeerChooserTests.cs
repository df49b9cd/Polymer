using NSubstitute;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class TwoRandomPeerChooserTests
{
    private static RequestMeta Meta() => new RequestMeta(service: "svc", transport: "http");

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask SinglePeer_Path()
    {
        var p = Substitute.For<IPeer>(); p.Identifier.Returns("p"); p.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null)); p.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var chooser = new TwoRandomPeerChooser(System.Collections.Immutable.ImmutableArray.Create(p));
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();
        res.Value.Peer.ShouldBeSameAs(p);
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask PicksLowerInflightOfTwo()
    {
        var a = Substitute.For<IPeer>(); a.Identifier.Returns("a"); a.Status.Returns(new PeerStatus(PeerState.Available, 5, null, null)); a.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var b = Substitute.For<IPeer>(); b.Identifier.Returns("b"); b.Status.Returns(new PeerStatus(PeerState.Available, 1, null, null)); b.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);
        var chooser = new TwoRandomPeerChooser([a, b], new Random(1));
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();
        res.Value.Peer.ShouldBeSameAs(b);
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Reject_ReturnsExhausted()
    {
        var a = Substitute.For<IPeer>(); a.Identifier.Returns("a"); a.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null)); a.TryAcquire(Arg.Any<CancellationToken>()).Returns(false);
        var chooser = new TwoRandomPeerChooser([a], new Random(1));
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(res.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);
    }
}
