namespace YARPCore.Core.Transport;

public interface ILifecycle
{
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
