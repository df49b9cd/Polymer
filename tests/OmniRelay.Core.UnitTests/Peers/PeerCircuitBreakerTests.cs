using System;
using OmniRelay.Core.Peers;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class PeerCircuitBreakerTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override long GetTimestamp() => System.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void SuspendsAfterFailures_ThenHalfOpen()
    {
        var tp = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new PeerCircuitBreakerOptions { FailureThreshold = 2, BaseDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(100), TimeProvider = tp };
        var cb = new PeerCircuitBreaker(options);

        Assert.True(cb.TryEnter()); // initial allowed
        cb.OnFailure(); // 1
        Assert.True(cb.TryEnter()); // still below threshold
        cb.OnFailure(); // >= threshold -> suspended
        Assert.True(cb.IsSuspended);
        var until = cb.SuspendedUntil;
        Assert.NotNull(until);

        // Before suspension end -> cannot enter
        Assert.False(cb.TryEnter());
        // Advance time beyond suspension
        tp.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(cb.TryEnter()); // enters half-open attempt 1
        cb.OnSuccess(); // success 1 but threshold=1 -> reset
        Assert.False(cb.IsSuspended);
        Assert.True(cb.TryEnter()); // open again
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void HalfOpenFailure_Resuspends()
    {
        var tp = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new PeerCircuitBreakerOptions { FailureThreshold = 1, BaseDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(100), HalfOpenMaxAttempts = 1, HalfOpenSuccessThreshold = 1, TimeProvider = tp };
        var cb = new PeerCircuitBreaker(options);

        cb.OnFailure();
        Assert.True(cb.IsSuspended);
        tp.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(cb.TryEnter()); // half-open attempt
        cb.OnFailure(); // immediate re-suspend
        Assert.True(cb.IsSuspended);
    }
}
