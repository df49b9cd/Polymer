using NSubstitute;
using OmniRelay.Core.Peers;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class PeerLeaseTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Dispose_ReleasesPeer_WithSuccessFlag()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var peer = Substitute.For<IPeer, IPeerTelemetry>();
        ((IPeer)peer).Identifier.Returns("p1");
        ((IPeer)peer).Status.Returns(new PeerStatus(PeerState.Available, 0, null, null));

        var lease = new PeerLease((IPeer)peer, meta);
        lease.MarkSuccess();
        await lease.DisposeAsync();

        ((IPeer)peer).Received(1).Release(true);
        ((IPeerTelemetry)peer).Received(1).RecordLeaseResult(true, Arg.Any<double>());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DefaultFailureFlag_Propagates()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var peer = Substitute.For<IPeer, IPeerTelemetry>();
        ((IPeer)peer).Identifier.Returns("p1");
        ((IPeer)peer).Status.Returns(new PeerStatus(PeerState.Available, 0, null, null));

        var lease = new PeerLease((IPeer)peer, meta);
        await lease.DisposeAsync();

        ((IPeer)peer).Received(1).Release(false);
        ((IPeerTelemetry)peer).Received(1).RecordLeaseResult(false, Arg.Any<double>());
    }
}
