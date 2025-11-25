using OmniRelay.Core.Transport;

namespace OmniRelay.Plugins.Internal.Transport;

/// <summary>Adapts any <see cref="ILifecycle"/> component to the <see cref="ITransport"/> contract expected by the data-plane host.</summary>
internal sealed class LifecycleTransportAdapter : ITransport
{
    private readonly ILifecycle _inner;

    public LifecycleTransportAdapter(string name, ILifecycle inner)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Name { get; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);
}
