using System.Threading;
using System.Threading.Tasks;
using Hugo;

namespace OmniRelay.Dispatcher;

public static class DispatcherExtensions
{
    public static async Task StartOrThrowAsync(this Dispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        var result = await dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }
    }

    public static async Task StopOrThrowAsync(this Dispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        var result = await dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }
    }
}
