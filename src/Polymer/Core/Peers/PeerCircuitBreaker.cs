using System;

namespace Polymer.Core.Peers;

public sealed class PeerCircuitBreaker
{
    private readonly PeerCircuitBreakerOptions _options;
    private readonly double _baseDelayMilliseconds;
    private readonly double _maxDelayMilliseconds;
    private int _failureCount;
    private DateTimeOffset? _suspendedUntil;

    public PeerCircuitBreaker(PeerCircuitBreakerOptions? options = null)
    {
        _options = options ?? new PeerCircuitBreakerOptions();
        if (_options.BaseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException("Base delay must be positive.", nameof(options));
        }

        if (_options.MaxDelay < _options.BaseDelay)
        {
            throw new ArgumentException("Max delay must be greater than or equal to base delay.", nameof(options));
        }

        _baseDelayMilliseconds = _options.BaseDelay.TotalMilliseconds;
        _maxDelayMilliseconds = _options.MaxDelay.TotalMilliseconds;
    }

    public int FailureCount => _failureCount;

    public DateTimeOffset? SuspendedUntil => _suspendedUntil;

    public bool IsSuspended => _suspendedUntil is { } until && until > _options.TimeProvider.GetUtcNow();

    public bool TryEnter()
    {
        if (IsSuspended)
        {
            return false;
        }

        return true;
    }

    public void OnSuccess()
    {
        _failureCount = 0;
        _suspendedUntil = null;
    }

    public void OnFailure()
    {
        var now = _options.TimeProvider.GetUtcNow();
        _failureCount++;

        if (_failureCount < _options.FailureThreshold)
        {
            return;
        }

        var exponent = Math.Max(0, _failureCount - _options.FailureThreshold);
        var delay = Math.Min(_baseDelayMilliseconds * Math.Pow(2, exponent), _maxDelayMilliseconds);
        _suspendedUntil = now.AddMilliseconds(delay);
    }
}
