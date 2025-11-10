namespace OmniRelay.Dispatcher;

/// <summary>Describes the current backpressure state observed on the table lease queue.</summary>
public sealed record TableLeaseBackpressureSignal(
    bool IsActive,
    long PendingCount,
    DateTimeOffset ObservedAt,
    long? HighWatermark,
    long? LowWatermark);

/// <summary>Consumers implement this to adjust throttling or instrumentation when backpressure toggles.</summary>
public interface ITableLeaseBackpressureListener
{
    ValueTask OnBackpressureChanged(TableLeaseBackpressureSignal signal, CancellationToken cancellationToken);
}
