using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.IntegrationTests.Support;

public abstract class TransportIntegrationTest : IntegrationTest
{
    protected TransportIntegrationTest(ITestOutputHelper output)
        : base(output)
    {
    }

    protected Task<DispatcherHost> StartDispatcherAsync(
        string name,
        Dispatcher.Dispatcher dispatcher,
        CancellationToken cancellationToken,
        bool ownsLifetime = true) =>
        DispatcherHost.StartAsync(name, dispatcher, LoggerFactory, cancellationToken, ownsLifetime);

    protected Task<DispatcherHost> StartDispatcherAsync(
        string name,
        DispatcherOptions options,
        CancellationToken cancellationToken,
        bool ownsLifetime = true) =>
        DispatcherHost.StartAsync(name, options, LoggerFactory, cancellationToken, ownsLifetime);
}
