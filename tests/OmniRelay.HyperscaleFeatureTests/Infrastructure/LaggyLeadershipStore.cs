using OmniRelay.Core.Leadership;

namespace OmniRelay.HyperscaleFeatureTests.Infrastructure;

/// <summary>
/// Leadership store wrapper that introduces configurable latency and transient failures to
/// simulate degraded registry links in hyperscale scenarios.
/// </summary>
internal sealed class LaggyLeadershipStore : ILeadershipStore
{
    private readonly InMemoryLeadershipStore _inner;
    private readonly object _randomLock = new();
    private readonly Random _random = new(2024);
    private int _latencyMilliseconds;
    private double _faultProbability;

    public LaggyLeadershipStore(TimeProvider? timeProvider = null)
    {
        _inner = new InMemoryLeadershipStore(timeProvider);
    }

    /// <summary>Gets or sets the additional latency applied to every store call.</summary>
    public TimeSpan AdditionalLatency
    {
        get => TimeSpan.FromMilliseconds(Volatile.Read(ref _latencyMilliseconds));
        set => Volatile.Write(ref _latencyMilliseconds, (int)Math.Max(0, value.TotalMilliseconds));
    }

    /// <summary>Configures the probability (0-1) of returning a store-unavailable failure.</summary>
    public void ConfigureFaultProbability(double probability)
    {
        Volatile.Write(ref _faultProbability, Math.Clamp(probability, 0d, 1d));
    }

    public async ValueTask<LeadershipLeaseRecord?> GetAsync(string scope, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LeadershipLeaseResult> TryAcquireAsync(string scope, string candidateId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken).ConfigureAwait(false);
        if (ShouldInjectFault())
        {
            return LeadershipLeaseResult.Failure(LeadershipLeaseFailureReason.StoreUnavailable);
        }

        return await _inner.TryAcquireAsync(scope, candidateId, leaseDuration, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LeadershipLeaseResult> TryRenewAsync(string scope, LeadershipLeaseRecord lease, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken).ConfigureAwait(false);
        if (ShouldInjectFault())
        {
            return LeadershipLeaseResult.Failure(LeadershipLeaseFailureReason.StoreUnavailable);
        }

        return await _inner.TryRenewAsync(scope, lease, leaseDuration, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> TryReleaseAsync(string scope, LeadershipLeaseRecord lease, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken).ConfigureAwait(false);
        if (ShouldInjectFault())
        {
            return false;
        }

        return await _inner.TryReleaseAsync(scope, lease, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SimulateLatencyAsync(CancellationToken cancellationToken)
    {
        var latency = Volatile.Read(ref _latencyMilliseconds);
        if (latency <= 0)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(latency), cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldInjectFault()
    {
        var probability = Volatile.Read(ref _faultProbability);
        if (probability <= 0)
        {
            return false;
        }

        lock (_randomLock)
        {
            return _random.NextDouble() < probability;
        }
    }
}
