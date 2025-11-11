using System.Collections.Immutable;
using OmniRelay.Core;
using OmniRelay.Core.Peers;
using Xunit;

namespace OmniRelay.Tests.Core.Peers;

public sealed class TwoRandomPeerChooserTests
{
    [Fact]
    public async Task AcquireAsync_ChoosesAvailablePeer()
    {
        var busy = new TestPeer("busy", inflight: 5);
        var idle = new TestPeer("idle", inflight: 0);
        var chooser = new TwoRandomPeerChooser([busy, idle], new DeterministicRandom(1, 0));
        var meta = new RequestMeta(service: "svc");

        var lease = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(lease.IsSuccess);
        Assert.Equal("idle", lease.Value.Peer.Identifier);
        await lease.Value.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WhenSinglePeer_ReturnsLease()
    {
        var peer = new TestPeer("single");
        var chooser = new TwoRandomPeerChooser([peer], new DeterministicRandom(0));
        var meta = new RequestMeta(service: "svc");

        var lease = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(lease.IsSuccess);
        Assert.Equal("single", lease.Value.Peer.Identifier);
        await lease.Value.DisposeAsync();
    }

    private sealed class TestPeer(string id, int inflight = 0, int maxConcurrency = int.MaxValue) : IPeer
    {
        private readonly int _maxConcurrency = maxConcurrency;
        private int _inflight = inflight;

        public string Identifier { get; } = id;

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

    private sealed class DeterministicRandom(params int[] sequence) : Random
    {
        private readonly int[] _sequence = sequence.Length == 0 ? [0] : sequence;
        private int _index;

        public override int Next(int maxValue) => _sequence[_index++ % _sequence.Length] % maxValue;
    }
}
