using NSubstitute;
using OmniRelay.Core.Peers;
using OmniRelay.Diagnostics;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class RoundRobinPeerChooserTests
{
    private static RequestMeta Meta() => new RequestMeta(service: "svc", transport: "http");

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask EmptyPeers_ReturnsUnavailable()
    {
        var chooser = new RoundRobinPeerChooser(System.Collections.Immutable.ImmutableArray<IPeer>.Empty);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(res.Error!).ShouldBe(OmniRelayStatusCode.Unavailable);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Acquires_From_First_Available()
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
        res.IsSuccess.ShouldBeTrue();
        res.Value.Peer.ShouldBeSameAs(p1);
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AllBusy_ReturnsResourceExhausted()
    {
        var p1 = Substitute.For<IPeer>(); p1.Identifier.Returns("p1"); p1.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null)); p1.TryAcquire(Arg.Any<CancellationToken>()).Returns(false);
        var p2 = Substitute.For<IPeer>(); p2.Identifier.Returns("p2"); p2.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null)); p2.TryAcquire(Arg.Any<CancellationToken>()).Returns(false);
        var chooser = new RoundRobinPeerChooser(p1, p2);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(res.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask HealthProviderSkipsIneligiblePeers()
    {
        var unhealthy = Substitute.For<IPeer>();
        unhealthy.Identifier.Returns("p1");
        unhealthy.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null));
        unhealthy.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var healthy = Substitute.For<IPeer>();
        healthy.Identifier.Returns("p2");
        healthy.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null));
        healthy.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var provider = Substitute.For<IPeerHealthSnapshotProvider>();
        provider.IsPeerEligible("p1").Returns(false);
        provider.IsPeerEligible("p2").Returns(true);
        provider.Snapshot().Returns([]);

        var chooser = new RoundRobinPeerChooser([unhealthy, healthy], provider);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();
        res.Value.Peer.ShouldBeSameAs(healthy);
        await res.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask HealthProviderAllIneligible_ReturnsResourceExhausted()
    {
        var p1 = Substitute.For<IPeer>();
        p1.Identifier.Returns("p1");
        p1.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null));
        p1.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var p2 = Substitute.For<IPeer>();
        p2.Identifier.Returns("p2");
        p2.Status.Returns(new PeerStatus(PeerState.Available, 0, null, null));
        p2.TryAcquire(Arg.Any<CancellationToken>()).Returns(true);

        var provider = Substitute.For<IPeerHealthSnapshotProvider>();
        provider.IsPeerEligible(Arg.Any<string>()).Returns(false);
        provider.Snapshot().Returns([]);

        var chooser = new RoundRobinPeerChooser([p1, p2], provider);
        var res = await chooser.AcquireAsync(Meta(), TestContext.Current.CancellationToken);
        res.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(res.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);
    }
}
