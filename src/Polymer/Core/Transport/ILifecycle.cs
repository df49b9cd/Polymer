using System.Threading;
using System.Threading.Tasks;

namespace Polymer.Core.Transport;

public interface ILifecycle
{
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
