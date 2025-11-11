namespace OmniRelay.Dispatcher;

/// <summary>Describes the current backpressure state observed on the resource lease queue.</summary>
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

/// <summary>Consumers implement this to adjust throttling or instrumentation when backpressure toggles.</summary>
public interface IResourceLeaseBackpressureListener
{
    ValueTask OnBackpressureChanged(ResourceLeaseBackpressureSignal signal, CancellationToken cancellationToken);
}
