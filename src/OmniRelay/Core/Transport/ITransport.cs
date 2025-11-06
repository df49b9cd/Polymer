namespace OmniRelay.Core.Transport;

/// <summary>
/// Marker interface for transports with a display name and lifecycle.
/// </summary>
public interface ITransport : ILifecycle
{
    /// <summary>Gets the transport name.</summary>
    string Name { get; }
}
