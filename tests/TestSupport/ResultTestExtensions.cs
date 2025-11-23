using OmniRelay.Dispatcher;

namespace OmniRelay.TestSupport;

public static class ResultTestExtensions
{
    public static async Task StartAsyncChecked(this OmniRelay.Dispatcher.Dispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        var result = await dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    public static async Task StopAsyncChecked(this OmniRelay.Dispatcher.Dispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        var result = await dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    public static ClientConfiguration ClientConfigChecked(this OmniRelay.Dispatcher.Dispatcher dispatcher, string service)
    {
        var result = dispatcher.ClientConfig(service);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    public static T ValueOrChecked<T>(this Hugo.Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }
}
