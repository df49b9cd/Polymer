using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Polymer.Core;
using Polymer.Core.Peers;
using Xunit;
using static Hugo.Go;

namespace Polymer.Tests.Core.Peers;

public sealed class TwoRandomPeerChooserTests
{
    [Fact]
    public async Task AcquireAsync_ChoosesAvailablePeer()
    {
        var busy = new TestPeer("busy", inflight: 5);
        var idle = new TestPeer("idle", inflight: 0);
        var chooser = new TwoRandomPeerChooser(ImmutableArray.Create<IPeer>(busy, idle), new DeterministicRandom(1, 0));
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
        var chooser = new TwoRandomPeerChooser(ImmutableArray.Create<IPeer>(peer), new DeterministicRandom(0));
        var meta = new RequestMeta(service: "svc");

        var lease = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(lease.IsSuccess);
        Assert.Equal("single", lease.Value.Peer.Identifier);
        await lease.Value.DisposeAsync();
    }

    private sealed class TestPeer : IPeer
    {
        private readonly int _maxConcurrency;
        private int _inflight;

        public TestPeer(string id, int inflight = 0, int maxConcurrency = int.MaxValue)
        {
            Identifier = id;
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

    private sealed class DeterministicRandom : System.Random
    {
        private readonly int[] _sequence;
        private int _index;

        public DeterministicRandom(params int[] sequence)
        {
            _sequence = sequence.Length == 0 ? new[] { 0 } : sequence;
        }

        public override int Next(int maxValue) => _sequence[_index++ % _sequence.Length] % maxValue;
    }
}
