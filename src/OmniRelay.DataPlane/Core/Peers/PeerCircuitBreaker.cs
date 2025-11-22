
namespace OmniRelay.Core.Peers;

/// <summary>
/// Simple circuit breaker for peers with exponential backoff suspension and half-open state.
/// </summary>
public sealed class PeerCircuitBreaker
{
    private readonly PeerCircuitBreakerOptions _options;
    private readonly double _baseDelayMilliseconds;
    private readonly double _maxDelayMilliseconds;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private int _failureCount;
    private DateTimeOffset? _suspendedUntil;
    private bool _isHalfOpen;
    private int _halfOpenAttempts;
    private int _halfOpenSuccesses;

    /// <summary>Creates a circuit breaker with the provided options.</summary>
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

        if (_options.FailureThreshold <= 0)
        {
            throw new ArgumentException("Failure threshold must be positive.", nameof(options));
        }

        if (_options.HalfOpenMaxAttempts <= 0)
        {
            throw new ArgumentException("Half-open max attempts must be positive.", nameof(options));
        }

        if (_options.HalfOpenSuccessThreshold <= 0)
        {
            throw new ArgumentException("Half-open success threshold must be positive.", nameof(options));
        }

        _baseDelayMilliseconds = _options.BaseDelay.TotalMilliseconds;
        _maxDelayMilliseconds = _options.MaxDelay.TotalMilliseconds;
        _timeProvider = _options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the current consecutive failure count.</summary>
    public int FailureCount
    {
        get
        {
            lock (_lock)
            {
                return _failureCount;
            }
        }
    }

    /// <summary>Gets when the breaker will exit suspension, if suspended.</summary>
    public DateTimeOffset? SuspendedUntil
    {
        get
        {
            lock (_lock)
            {
                return _suspendedUntil;
            }
        }
    }

    /// <summary>Gets a value indicating whether the breaker is currently suspended.</summary>
    public bool IsSuspended
    {
        get
        {
            var now = _options.TimeProvider.GetUtcNow();
            lock (_lock)
            {
                return _suspendedUntil is { } until && until > now;
            }
        }
    }

    /// <summary>
    /// Attempts to enter the peer for a request, honoring suspension and half-open limits.
    /// </summary>
    public bool TryEnter()
    {
        var now = _options.TimeProvider.GetUtcNow();

        lock (_lock)
        {
            if (_suspendedUntil is not { } until)
            {
                return true;
            }

            if (until > now)
            {
                return false;
            }

            if (!_isHalfOpen)
            {
                EnterHalfOpenLocked();
            }

            if (_halfOpenAttempts >= _options.HalfOpenMaxAttempts)
            {
                return false;
            }

            _halfOpenAttempts++;

            return true;
        }
    }

    /// <summary>Records a successful attempt, potentially resetting the breaker or progressing half-open.</summary>
    public void OnSuccess()
    {
        lock (_lock)
        {
            if (_isHalfOpen)
            {
                _halfOpenSuccesses++;
                if (_halfOpenSuccesses < _options.HalfOpenSuccessThreshold)
                {
                    return;
                }
            }

            ResetLocked();
        }
    }

    /// <summary>Records a failed attempt and may transition to suspended or half-open states.</summary>
    public void OnFailure()
    {
        var now = _options.TimeProvider.GetUtcNow();

        lock (_lock)
        {
            if (_isHalfOpen)
            {
                _isHalfOpen = false;
                _halfOpenAttempts = 0;
                _halfOpenSuccesses = 0;
                _failureCount = Math.Max(_failureCount + 1, _options.FailureThreshold);
                ScheduleSuspensionLocked(now);
                return;
            }

            _failureCount++;

            if (_failureCount < _options.FailureThreshold)
            {
                return;
            }

            ScheduleSuspensionLocked(now);
        }
    }

    private void EnterHalfOpenLocked()
    {
        _isHalfOpen = true;
        _halfOpenAttempts = 0;
        _halfOpenSuccesses = 0;
    }

    private void ResetLocked()
    {
        _failureCount = 0;
        _suspendedUntil = null;
        _isHalfOpen = false;
        _halfOpenAttempts = 0;
        _halfOpenSuccesses = 0;
    }

    private void ScheduleSuspensionLocked(DateTimeOffset now)
    {
        _isHalfOpen = false;
        _halfOpenAttempts = 0;
        _halfOpenSuccesses = 0;

        var exponent = Math.Max(0, _failureCount - _options.FailureThreshold);
        var delay = Math.Min(_baseDelayMilliseconds * Math.Pow(2, exponent), _maxDelayMilliseconds);
        _suspendedUntil = now.AddMilliseconds(delay);
    }
}
