using System.Threading.Channels;

namespace OmniRelay.ControlPlane.Events;

/// <summary>Represents a subscriber on the control-plane event bus.</summary>
public sealed class ControlPlaneEventSubscription : IAsyncDisposable
{
    private readonly ControlPlaneEventBus _owner;
    private readonly long _subscriptionId;
    private bool _disposed;

    internal ControlPlaneEventSubscription(ControlPlaneEventBus owner, long subscriptionId, ChannelReader<ControlPlaneEvent> reader)
    {
        _owner = owner;
        _subscriptionId = subscriptionId;
        Reader = reader;
    }

    public ChannelReader<ControlPlaneEvent> Reader { get; }

    public IAsyncEnumerable<ControlPlaneEvent> ReadAllAsync(CancellationToken cancellationToken = default) =>
        Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _owner.Unsubscribe(_subscriptionId);
        return ValueTask.CompletedTask;
    }
}
