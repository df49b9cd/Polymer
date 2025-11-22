namespace OmniRelay.ControlPlane.Throttling;

/// <summary>Describes the current backpressure state observed by control-plane services.</summary>
public sealed record ResourceLeaseBackpressureSignal(
    bool IsActive,
    long PendingCount,
    DateTimeOffset ObservedAt,
    long? HighWatermark,
    long? LowWatermark)
{
    public bool IsActive { get; init; } = IsActive;

    public long PendingCount { get; init; } = PendingCount;

    public DateTimeOffset ObservedAt { get; init; } = ObservedAt;

    public long? HighWatermark { get; init; } = HighWatermark;

    public long? LowWatermark { get; init; } = LowWatermark;
}

/// <summary>Consumers implement this to adjust throttling/telemetry when backpressure toggles.</summary>
public interface IResourceLeaseBackpressureListener
{
    ValueTask OnBackpressureChanged(ResourceLeaseBackpressureSignal signal, CancellationToken cancellationToken);
}
