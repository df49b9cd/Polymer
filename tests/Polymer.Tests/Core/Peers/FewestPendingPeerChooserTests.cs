using System.Threading;
using System.Threading.Tasks;
using Polymer.Core;
using Polymer.Core.Peers;
using Polymer.Errors;
using Xunit;
using static Hugo.Go;

namespace Polymer.Tests.Core.Peers;

public sealed class FewestPendingPeerChooserTests
{
    [Fact]
    public async Task AcquireAsync_PicksPeerWithFewestInflight()
    {
        var busyPeer = new TestPeer("busy", inflight: 5);
        var idlePeer = new TestPeer("idle", inflight: 1);
        var chooser = new FewestPendingPeerChooser(busyPeer, idlePeer);
        var meta = new RequestMeta(service: "svc");

        var lease = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(lease.IsSuccess);
        Assert.Equal("idle", lease.Value.Peer.Identifier);
        await lease.Value.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WhenAllBusy_ReturnsResourceExhausted()
    {
        var peer1 = new TestPeer("p1", inflight: 3, maxConcurrency: 3);
        var peer2 = new TestPeer("p2", inflight: 2, maxConcurrency: 2);
        var chooser = new FewestPendingPeerChooser(peer1, peer2);
        var meta = new RequestMeta(service: "svc");

        var lease = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(lease.IsFailure);
        Assert.Equal(PolymerStatusCode.ResourceExhausted, PolymerErrorAdapter.ToStatus(lease.Error!));
    }

    private sealed class TestPeer : IPeer
    {
        private readonly int _maxConcurrency;
        private int _inflight;

        public TestPeer(string identifier, int inflight = 0, int maxConcurrency = int.MaxValue)
        {
            Identifier = identifier;
            _inflight = inflight;
            _maxConcurrency = maxConcurrency;
        }

        public string Identifier { get; }

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

        public void Release(bool success)
        {
            Interlocked.Decrement(ref _inflight);
        }
    }
}
