namespace OmniRelay.ControlPlane.Events;

/// <summary>Provides a process-local event bus for control-plane lifecycle updates.</summary>
public interface IControlPlaneEventBus
{
    ControlPlaneEventSubscription Subscribe(ControlPlaneEventFilter? filter = null, int capacity = 256);

    ValueTask PublishAsync(ControlPlaneEvent message, CancellationToken cancellationToken = default);
}
