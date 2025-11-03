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
            BaseDelay = TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromSeconds(1),
            FailureThreshold = 1,
            TimeProvider = provider
        };
        var breaker = new PeerCircuitBreaker(options);

        Assert.True(breaker.TryEnter());
        breaker.OnFailure();
        Assert.True(breaker.IsSuspended);
        Assert.False(breaker.TryEnter());

        provider.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(breaker.IsSuspended);
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void Success_ResetsFailures()
    {
        var provider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var breaker = new PeerCircuitBreaker(new PeerCircuitBreakerOptions { TimeProvider = provider, FailureThreshold = 1 });

        breaker.OnFailure();
        Assert.True(breaker.IsSuspended);

        provider.Advance(TimeSpan.FromSeconds(1));
        breaker.OnSuccess();

        Assert.False(breaker.IsSuspended);
        Assert.True(breaker.TryEnter());
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public TestTimeProvider(DateTimeOffset initial)
        {
            _now = initial;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta)
        {
            _now = _now.Add(delta);
        }

        public override long GetTimestamp() => throw new NotImplementedException();
    }
}
