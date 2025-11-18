using OmniRelay.Core.Peers;
using Xunit;

namespace OmniRelay.Tests.Core.Peers;

public sealed class PeerCircuitBreakerTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Failure_TriggersSuspension()
    {
        var provider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var options = new PeerCircuitBreakerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(200),
            FailureThreshold = 1,
            TimeProvider = provider
        };
        var breaker = new PeerCircuitBreaker(options);

        breaker.TryEnter().ShouldBeTrue();
        breaker.OnFailure();
        breaker.TryEnter().ShouldBeFalse();

        provider.Advance(TimeSpan.FromMilliseconds(150));
        breaker.TryEnter().ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Success_ResetsFailures()
    {
        var provider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var breaker = new PeerCircuitBreaker(new PeerCircuitBreakerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            FailureThreshold = 1,
            TimeProvider = provider
        });

        breaker.OnFailure();
        breaker.TryEnter().ShouldBeFalse();

        breaker.OnSuccess();
        breaker.TryEnter().ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void HalfOpen_AllowsLimitedProbes()
    {
        var provider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var breaker = new PeerCircuitBreaker(new PeerCircuitBreakerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(200),
            FailureThreshold = 1,
            HalfOpenMaxAttempts = 1,
            TimeProvider = provider
        });

        breaker.OnFailure();
        breaker.TryEnter().ShouldBeFalse();

        provider.Advance(TimeSpan.FromMilliseconds(150));
        breaker.TryEnter().ShouldBeTrue(); // first probe allowed
        breaker.TryEnter().ShouldBeFalse(); // additional probes rejected until outcome reported

        breaker.OnSuccess();
        breaker.TryEnter().ShouldBeTrue(); // breaker resets after success
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void HalfOpen_RequiresMultipleSuccessesWhenConfigured()
    {
        var provider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var breaker = new PeerCircuitBreaker(new PeerCircuitBreakerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            FailureThreshold = 1,
            HalfOpenMaxAttempts = 2,
            HalfOpenSuccessThreshold = 2,
            TimeProvider = provider
        });

        breaker.OnFailure();
        provider.Advance(TimeSpan.FromMilliseconds(150));

        breaker.TryEnter().ShouldBeTrue();
        breaker.OnSuccess(); // first probe succeeds but breaker stays half-open

        breaker.TryEnter().ShouldBeTrue(); // second probe permitted
        breaker.OnSuccess(); // second success closes the breaker

        breaker.TryEnter().ShouldBeTrue(); // closed path allows entry freely
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void HalfOpen_FailureReopensBreaker()
    {
        var provider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var breaker = new PeerCircuitBreaker(new PeerCircuitBreakerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(400),
            FailureThreshold = 1,
            HalfOpenMaxAttempts = 1,
            TimeProvider = provider
        });

        breaker.OnFailure();
        provider.Advance(TimeSpan.FromMilliseconds(150));

        breaker.TryEnter().ShouldBeTrue();
        breaker.OnFailure(); // failure during half-open should resuspend

        breaker.TryEnter().ShouldBeFalse();
        provider.Advance(TimeSpan.FromMilliseconds(200));
        breaker.TryEnter().ShouldBeTrue();
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override long GetTimestamp() => throw new NotImplementedException();
    }
}
