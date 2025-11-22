namespace OmniRelay.Dispatcher;

/// <summary>
/// Implemented by components that require a reference to the owning dispatcher.
/// </summary>
public interface IDispatcherAware
{
    /// <summary>Binds the dispatcher to the component.</summary>
    void Bind(Dispatcher dispatcher);
}
