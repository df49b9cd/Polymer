using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Core.Peers;

public sealed class PeerListCoordinatorTests
{
    [Fact]
    public async Task AcquireAsync_AllPeersBusyWithoutDeadline_ReturnsResourceExhausted()
    {
        using var coordinator = new PeerListCoordinator([
            new TestPeer("peer-1", PeerState.Available, maxConcurrency: 0),
            new TestPeer("peer-2", PeerState.Available, maxConcurrency: 0)
        ]);

        var meta = new RequestMeta(service: "svc");

        var result = await coordinator.AcquireAsync(meta, CancellationToken.None, SelectFirst);

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public async Task AcquireAsync_NoAvailabilityBeforeDeadline_ReturnsDeadlineExceeded()
    {
        using var coordinator = new PeerListCoordinator([
            new TestPeer("peer-1", PeerState.Unavailable)
        ]);

        var meta = new RequestMeta(service: "svc", deadline: DateTimeOffset.UtcNow.AddMilliseconds(-1));

        var result = await coordinator.AcquireAsync(meta, CancellationToken.None, SelectFirst);

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    private static IPeer? SelectFirst(IReadOnlyList<IPeer> peers) =>
        peers.Count > 0 ? peers[0] : null;

    private sealed class TestPeer : IPeer
    {
        private readonly int _maxConcurrency;
        private readonly PeerState _state;
        private int _inflight;

        public TestPeer(string identifier, PeerState state, int maxConcurrency = int.MaxValue)
        {
            Identifier = identifier;
            _state = state;
            _maxConcurrency = maxConcurrency;
        }

        public string Identifier { get; }

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
