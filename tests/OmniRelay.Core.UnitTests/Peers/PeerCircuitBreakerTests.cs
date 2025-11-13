using OmniRelay.Core.Peers;
using Xunit;

namespace OmniRelay.Core.UnitTests.Peers;

public class PeerCircuitBreakerTests
{
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
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

        cb.TryEnter().ShouldBeTrue(); // initial allowed
        cb.OnFailure(); // 1
        cb.TryEnter().ShouldBeTrue(); // still below threshold
        cb.OnFailure(); // >= threshold -> suspended
        cb.IsSuspended.ShouldBeTrue();
        var until = cb.SuspendedUntil;
        until.ShouldNotBeNull();

        // Before suspension end -> cannot enter
        cb.TryEnter().ShouldBeFalse();
        // Advance time beyond suspension
        tp.Advance(TimeSpan.FromMilliseconds(200));
        cb.TryEnter().ShouldBeTrue(); // enters half-open attempt 1
        cb.OnSuccess(); // success 1 but threshold=1 -> reset
        cb.IsSuspended.ShouldBeFalse();
        cb.TryEnter().ShouldBeTrue(); // open again
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void HalfOpenFailure_Resuspends()
    {
        var tp = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new PeerCircuitBreakerOptions { FailureThreshold = 1, BaseDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(100), HalfOpenMaxAttempts = 1, HalfOpenSuccessThreshold = 1, TimeProvider = tp };
        var cb = new PeerCircuitBreaker(options);

        cb.OnFailure();
        cb.IsSuspended.ShouldBeTrue();
        tp.Advance(TimeSpan.FromMilliseconds(200));
        cb.TryEnter().ShouldBeTrue(); // half-open attempt
        cb.OnFailure(); // immediate re-suspend
        cb.IsSuspended.ShouldBeTrue();
    }
}
