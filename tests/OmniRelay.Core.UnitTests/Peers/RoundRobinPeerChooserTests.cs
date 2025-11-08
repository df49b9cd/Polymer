using System;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Peers;

public class RoundRobinPeerChooserTests
{
    private static RequestMeta Meta() => new RequestMeta(service: "svc", transport: "http");

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task EmptyPeers_ReturnsUnavailable()
    {
        var chooser = new RoundRobinPeerChooser(System.Collections.Immutable.ImmutableArray<IPeer>.Empty);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        Assert.True(res.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Unavailable, OmniRelayErrorAdapter.ToStatus(res.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Acquires_From_First_Available()
    {
        var p1 = Substitute.For<IPeer>();
        p1.Identifier.Returns("p1");
        p1.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null));
        p1.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var p2 = Substitute.For<IPeer>();
        p2.Identifier.Returns("p2");
        p2.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null));
        p2.TryAcquire(Arg.Any<CancellationToken>()).Returns(false);

        var chooser = new RoundRobinPeerChooser(p1, p2);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        Assert.True(res.IsSuccess);
        Assert.Same(p1, res.Value.Peer);
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AllBusy_ReturnsResourceExhausted()
    {
        var p1 = Substitute.For<IPeer>(); p1.Identifier.Returns("p1"); p1.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null)); p1.TryAcquire(Arg.Any<CancellationToken>()).Returns(false);
        var p2 = Substitute.For<IPeer>(); p2.Identifier.Returns("p2"); p2.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null)); p2.TryAcquire(Arg.Any<CancellationToken>()).Returns(false);
        var chooser = new RoundRobinPeerChooser(p1, p2);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        Assert.True(res.IsFailure);
        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(res.Error!));
    }
}
