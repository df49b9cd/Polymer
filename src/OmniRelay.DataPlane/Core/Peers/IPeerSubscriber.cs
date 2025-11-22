namespace OmniRelay.Core.Peers;

/// <summary>
/// Receives peer status change notifications from observable peers.
/// </summary>
public interface IPeerSubscriber
{
    /// <summary>Invoked when the peer's status or load changes.</summary>
    void NotifyStatusChanged(IPeer peer);
}
