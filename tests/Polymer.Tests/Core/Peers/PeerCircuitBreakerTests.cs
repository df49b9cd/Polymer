using System;
using System.Threading;
using System.Threading.Tasks;
using Polymer.Core.Peers;
using Xunit;

namespace Polymer.Tests.Core.Peers;

public sealed class PeerCircuitBreakerTests
{
    [Fact]
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

        Assert.True(breaker.TryEnter());
        breaker.OnFailure();
        Assert.False(breaker.TryEnter());

        provider.Advance(TimeSpan.FromMilliseconds(150));
        Assert.True(breaker.TryEnter());
    }

    [Fact]
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
        Assert.False(breaker.TryEnter());

        breaker.OnSuccess();
        Assert.True(breaker.TryEnter());
    }

    [Fact]
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
        Assert.False(breaker.TryEnter());

        provider.Advance(TimeSpan.FromMilliseconds(150));
        Assert.True(breaker.TryEnter()); // first probe allowed
        Assert.False(breaker.TryEnter()); // additional probes rejected until outcome reported

        breaker.OnSuccess();
        Assert.True(breaker.TryEnter()); // breaker resets after success
    }

    [Fact]
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

        Assert.True(breaker.TryEnter());
        breaker.OnSuccess(); // first probe succeeds but breaker stays half-open

        Assert.True(breaker.TryEnter()); // second probe permitted
        breaker.OnSuccess(); // second success closes the breaker

        Assert.True(breaker.TryEnter()); // closed path allows entry freely
    }

    [Fact]
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

        Assert.True(breaker.TryEnter());
        breaker.OnFailure(); // failure during half-open should resuspend

        Assert.False(breaker.TryEnter());
        provider.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(breaker.TryEnter());
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override long GetTimestamp() => throw new NotImplementedException();
    }
}
