using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Core.Peers;

public sealed class PeerListCoordinatorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AcquireAsync_AllPeersBusyWithoutDeadline_ReturnsResourceExhausted()
    {
        using var coordinator = new PeerListCoordinator([
            new TestPeer("peer-1", PeerState.Available, maxConcurrency: 0),
            new TestPeer("peer-2", PeerState.Available, maxConcurrency: 0)
        ]);

        var meta = new RequestMeta(service: "svc");

        var result = await coordinator.AcquireAsync(meta, CancellationToken.None, SelectFirst);

        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AcquireAsync_NoAvailabilityBeforeDeadline_ReturnsDeadlineExceeded()
    {
        using var coordinator = new PeerListCoordinator([
            new TestPeer("peer-1", PeerState.Unavailable)
        ]);

        var meta = new RequestMeta(service: "svc", deadline: DateTimeOffset.UtcNow.AddMilliseconds(-1));

        var result = await coordinator.AcquireAsync(meta, CancellationToken.None, SelectFirst);

        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.DeadlineExceeded);
    }

    private static IPeer? SelectFirst(IReadOnlyList<IPeer> peers) =>
        peers.Count > 0 ? peers[0] : null;

    private sealed class TestPeer(string identifier, PeerState state, int maxConcurrency = int.MaxValue)
        : IPeer
    {
        private readonly int _maxConcurrency = maxConcurrency;
        private readonly PeerState _state = state;
        private int _inflight;

        public string Identifier { get; } = identifier;

        public PeerStatus Status => new(_state, _inflight, null, null);

        public bool TryAcquire(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_state != PeerState.Available)
            {
                return false;
            }

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
            if (_state != PeerState.Available)
            {
                return;
            }

            Interlocked.Decrement(ref _inflight);
        }
    }
}
