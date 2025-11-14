using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Events;

/// <summary>In-process event bus shared by control-plane services (gossip, leadership, diagnostics).</summary>
public sealed partial class ControlPlaneEventBus : IControlPlaneEventBus, IDisposable
{
    private readonly ConcurrentDictionary<long, Subscriber> _subscribers = new();
    private readonly ILogger<ControlPlaneEventBus> _logger;
    private long _nextSubscriptionId;
    private bool _disposed;

    public ControlPlaneEventBus(ILogger<ControlPlaneEventBus> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ControlPlaneEventSubscription Subscribe(ControlPlaneEventFilter? filter = null, int capacity = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ControlPlaneEventBus));

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Subscription capacity must be at least 1.");
        }

        var channel = Channel.CreateBounded<ControlPlaneEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var id = Interlocked.Increment(ref _nextSubscriptionId);
        _subscribers.TryAdd(id, new Subscriber(filter, channel));
        ControlPlaneEventBusLog.SubscriberAdded(_logger, id, filter?.EventType);
        return new ControlPlaneEventSubscription(this, id, channel.Reader);
    }

    public ValueTask PublishAsync(ControlPlaneEvent message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_disposed || _subscribers.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var (id, subscriber) in _subscribers)
        {
            if (!subscriber.ShouldDeliver(message))
            {
                continue;
            }

            if (!subscriber.Writer.TryWrite(message))
            {
                ControlPlaneEventBusLog.EventDropped(_logger, message.EventType, id);
            }
        }

        return ValueTask.CompletedTask;
    }

    internal void Unsubscribe(long subscriptionId)
    {
        if (_subscribers.TryRemove(subscriptionId, out var subscriber))
        {
            subscriber.Writer.TryComplete();
            ControlPlaneEventBusLog.SubscriberRemoved(_logger, subscriptionId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var id in _subscribers.Keys)
        {
            if (_subscribers.TryRemove(id, out var removed))
            {
                removed.Writer.TryComplete();
            }
        }
    }

    private sealed record Subscriber(ControlPlaneEventFilter? Filter, Channel<ControlPlaneEvent> Channel)
    {
        public ChannelWriter<ControlPlaneEvent> Writer => Channel.Writer;

        public bool ShouldDeliver(ControlPlaneEvent evt) =>
            Filter?.Matches(evt) ?? true;
    }

    private static partial class ControlPlaneEventBusLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Control-plane event subscriber {Id} connected (eventType={EventType}).")]
        public static partial void SubscriberAdded(ILogger logger, long id, string? eventType);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Control-plane event subscriber {Id} disconnected.")]
        public static partial void SubscriberRemoved(ILogger logger, long id);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Control-plane event {EventType} dropped for subscriber {Id} (channel full).")]
        public static partial void EventDropped(ILogger logger, string eventType, long id);
    }
}
