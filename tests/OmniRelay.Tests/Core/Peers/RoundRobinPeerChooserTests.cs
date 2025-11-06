using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Core.Peers;

public sealed class RoundRobinPeerChooserTests
{
    [Fact]
    public async Task AcquireAsync_RotatesAcrossPeers()
    {
        var peer1 = new TestPeer("peer-1");
        var peer2 = new TestPeer("peer-2");
        var chooser = new RoundRobinPeerChooser(peer1, peer2);
        var meta = new RequestMeta(service: "svc");

        var lease1 = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(lease1.IsSuccess);
        Assert.Equal("peer-1", lease1.Value.Peer.Identifier);
        await lease1.Value.DisposeAsync();

        var lease2 = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(lease2.IsSuccess);
        Assert.Equal("peer-2", lease2.Value.Peer.Identifier);
        await lease2.Value.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WhenAllBusy_ReturnsResourceExhausted()
    {
        var peer = new TestPeer("peer-1", maxConcurrency: 1);
        var chooser = new RoundRobinPeerChooser(peer);
        var meta = new RequestMeta(service: "svc");

        var first = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);

        var second = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(second.IsFailure);
        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(second.Error!));

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
