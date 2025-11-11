using Hugo;

namespace OmniRelay.Dispatcher;

public static class DispatcherExtensions
{
    public static async Task StartOrThrowAsync(this Dispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        var result = await dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }
    }

    public static async Task StopOrThrowAsync(this Dispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        var result = await dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }
    }

    public static ClientConfiguration ClientConfigOrThrow(this Dispatcher dispatcher, string service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        var result = dispatcher.ClientConfig(service);
        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }

        return result.Value;
    }
}
