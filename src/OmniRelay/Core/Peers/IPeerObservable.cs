namespace OmniRelay.Core.Peers;

/// <summary>
/// Optional capability implemented by peers that can propagate status changes to subscribers.
/// </summary>
public interface IPeerObservable
{
    /// <summary>
    /// Subscribes to peer status changes. The returned disposable should be disposed to unsubscribe.
    /// </summary>
    IDisposable Subscribe(IPeerSubscriber subscriber);
}
