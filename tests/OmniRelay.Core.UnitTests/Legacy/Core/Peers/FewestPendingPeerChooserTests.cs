using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Core.Peers;

public sealed class FewestPendingPeerChooserTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AcquireAsync_PicksPeerWithFewestInflight()
    {
        var busyPeer = new TestPeer("busy", inflight: 5);
        var idlePeer = new TestPeer("idle", inflight: 1);
        var chooser = new FewestPendingPeerChooser(busyPeer, idlePeer);
        var meta = new RequestMeta(service: "svc");

        var lease = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        lease.IsSuccess.ShouldBeTrue();
        lease.Value.Peer.Identifier.ShouldBe("idle");
        await lease.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AcquireAsync_WhenAllBusy_ReturnsResourceExhausted()
    {
        var peer1 = new TestPeer("p1", inflight: 3, maxConcurrency: 3);
        var peer2 = new TestPeer("p2", inflight: 2, maxConcurrency: 2);
        var chooser = new FewestPendingPeerChooser(peer1, peer2);
        var meta = new RequestMeta(service: "svc");

        var lease = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        lease.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(lease.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);
    }

    private sealed class TestPeer(string identifier, int inflight = 0, int maxConcurrency = int.MaxValue) : IPeer
    {
        private readonly int _maxConcurrency = maxConcurrency;
        private int _inflight = inflight;

        public string Identifier { get; } = identifier;

        public PeerStatus Status => new(PeerState.Available, _inflight, null, null);

        public bool TryAcquire(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (true)
            {
                var current = _inflight;
                if (current >= _maxConcurrency)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _inflight, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void Release(bool success) => Interlocked.Decrement(ref _inflight);
    }
}
