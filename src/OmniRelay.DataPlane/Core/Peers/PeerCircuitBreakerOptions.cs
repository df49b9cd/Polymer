namespace OmniRelay.Core.Peers;

/// <summary>
/// Configuration for the peer circuit breaker behavior and timings.
/// </summary>
public sealed class PeerCircuitBreakerOptions
{
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    public int FailureThreshold { get; init; } = 1;

    public int HalfOpenMaxAttempts { get; init; } = 1;

    public int HalfOpenSuccessThreshold { get; init; } = 1;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
