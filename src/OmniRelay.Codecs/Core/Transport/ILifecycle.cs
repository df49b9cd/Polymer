namespace OmniRelay.Core.Transport;

/// <summary>
/// Lifecycle contract for transport components that require start/stop.
/// </summary>
public interface ILifecycle
{
    /// <summary>Starts the component.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    /// <summary>Stops the component.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
