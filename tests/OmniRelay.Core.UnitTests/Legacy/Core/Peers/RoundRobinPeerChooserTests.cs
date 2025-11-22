using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Core.Peers;

public sealed class RoundRobinPeerChooserTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AcquireAsync_RotatesAcrossPeers()
    {
        var peer1 = new TestPeer("peer-1");
        var peer2 = new TestPeer("peer-2");
        var chooser = new RoundRobinPeerChooser(peer1, peer2);
        var meta = new RequestMeta(service: "svc");

        var lease1 = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        lease1.IsSuccess.ShouldBeTrue();
        lease1.Value.Peer.Identifier.ShouldBe("peer-1");
        await lease1.Value.DisposeAsync();

        var lease2 = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        lease2.IsSuccess.ShouldBeTrue();
        lease2.Value.Peer.Identifier.ShouldBe("peer-2");
        await lease2.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AcquireAsync_WhenAllBusy_ReturnsResourceExhausted()
    {
        var peer = new TestPeer("peer-1", maxConcurrency: 1);
        var chooser = new RoundRobinPeerChooser(peer);
        var meta = new RequestMeta(service: "svc");

        var first = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        first.IsSuccess.ShouldBeTrue();

        var second = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        second.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(second.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);

        await first.Value.DisposeAsync();
    }

    private sealed class TestPeer(string identifier, int maxConcurrency = int.MaxValue) : IPeer
    {
        private readonly int _maxConcurrency = maxConcurrency;
        private int _inflight;

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
