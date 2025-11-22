using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;

namespace OmniRelay.ControlPlane.Throttling;

/// <summary>
/// Provides a shared selector that toggles between normal and backpressure rate limiters.
/// Wire <see cref="SelectLimiter"/> into <see cref="Core.Middleware.RateLimitingOptions.LimiterSelector"/>.
/// </summary>
public sealed class BackpressureAwareRateLimiter
{
    private readonly RateLimiter? _normalLimiter;
    private readonly RateLimiter? _backpressureLimiter;
    private readonly Func<RequestMeta, RateLimiter?>? _normalSelector;
    private readonly Func<RequestMeta, RateLimiter?>? _backpressureSelector;
    private int _isBackpressureActive;

    public BackpressureAwareRateLimiter(
        RateLimiter? normalLimiter = null,
        RateLimiter? backpressureLimiter = null,
        Func<RequestMeta, RateLimiter?>? normalSelector = null,
        Func<RequestMeta, RateLimiter?>? backpressureSelector = null)
    {
        if (normalLimiter is null && normalSelector is null)
        {
            throw new ArgumentException(
                "Either a normal limiter instance or a normal selector delegate must be provided.",
                nameof(normalLimiter));
        }

        _normalLimiter = normalLimiter;
        _backpressureLimiter = backpressureLimiter;
        _normalSelector = normalSelector;
        _backpressureSelector = backpressureSelector;
    }

    /// <summary>
    /// Selects the appropriate limiter for the request (called from RateLimitingMiddleware).
    /// </summary>
    public RateLimiter? SelectLimiter(RequestMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (Volatile.Read(ref _isBackpressureActive) == 1)
        {
            var limiter = _backpressureSelector?.Invoke(meta) ?? _backpressureLimiter;
            if (limiter is not null)
            {
                return limiter;
            }
        }

        return _normalSelector?.Invoke(meta) ?? _normalLimiter;
    }

    /// <summary>Updates the backpressure flag and returns true when the state changed.</summary>
    public bool TrySetBackpressure(bool isActive)
    {
        var newValue = isActive ? 1 : 0;
        var previous = Interlocked.Exchange(ref _isBackpressureActive, newValue);
        return previous != newValue;
    }
}

/// <summary>
/// Sample listener that toggles <see cref="BackpressureAwareRateLimiter"/> and logs transitions.
/// </summary>
public sealed class RateLimitingBackpressureListener(BackpressureAwareRateLimiter rateLimiter, ILogger? logger = null)
    : IResourceLeaseBackpressureListener
{
    private readonly BackpressureAwareRateLimiter _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
    private readonly ILogger? _logger = logger;

    public ValueTask OnBackpressureChanged(ResourceLeaseBackpressureSignal signal, CancellationToken cancellationToken)
    {
        if (!_rateLimiter.TrySetBackpressure(signal.IsActive))
        {
            return ValueTask.CompletedTask;
        }

        if (_logger is null)
        {
            return ValueTask.CompletedTask;
        }

        if (signal.IsActive)
        {
            ResourceLeaseBackpressureListenersLog.BackpressureActivated(_logger, signal.PendingCount, signal.HighWatermark);
        }
        else
        {
            ResourceLeaseBackpressureListenersLog.BackpressureCleared(_logger, signal.PendingCount);
        }

        return ValueTask.CompletedTask;
    }
}

internal static partial class ResourceLeaseBackpressureListenersLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Resource lease backpressure activated: {Pending} pending (high watermark {High}).")]
    public static partial void BackpressureActivated(ILogger logger, long pending, long? high);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Resource lease backpressure cleared at {Pending} pending items.")]
    public static partial void BackpressureCleared(ILogger logger, long pending);
}

/// <summary>
/// Listener that records the latest backpressure signal and exposes a channel for control-plane streaming.
/// </summary>
public sealed class ResourceLeaseBackpressureDiagnosticsListener : IResourceLeaseBackpressureListener
{
    private readonly Channel<ResourceLeaseBackpressureSignal> _updates;
    private ResourceLeaseBackpressureSignal? _latest;

    public ResourceLeaseBackpressureDiagnosticsListener(int historyCapacity = 16)
    {
        if (historyCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity), "History capacity must be at least 1.");
        }

        _updates = Channel.CreateBounded<ResourceLeaseBackpressureSignal>(new BoundedChannelOptions(historyCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>Gets the last observed signal, if any.</summary>
    public ResourceLeaseBackpressureSignal? Latest => Volatile.Read(ref _latest);

    /// <summary>Channel reader that streams every transition for diagnostics endpoints.</summary>
    public ChannelReader<ResourceLeaseBackpressureSignal> Updates => _updates.Reader;

    /// <summary>
    /// Convenience helper to consume updates via await foreach.
    /// </summary>
    public IAsyncEnumerable<ResourceLeaseBackpressureSignal> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _updates.Reader.ReadAllAsync(cancellationToken);

    public ValueTask OnBackpressureChanged(ResourceLeaseBackpressureSignal signal, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _latest, signal);
        _updates.Writer.TryWrite(signal);
        return ValueTask.CompletedTask;
    }
}
