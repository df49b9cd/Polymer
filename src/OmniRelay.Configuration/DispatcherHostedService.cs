using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OmniRelay.Configuration;

internal sealed class DispatcherHostedService(Dispatcher.Dispatcher dispatcher, ILogger<DispatcherHostedService>? logger = null) : IHostedService
{
    private readonly Dispatcher.Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly ILogger<DispatcherHostedService> _logger = logger ?? NullLogger<DispatcherHostedService>.Instance;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
        _logger.LogInformation("Starting OmniRelay dispatcher for service {ServiceName}", _dispatcher.ServiceName);
        }

        await _dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
        _logger.LogInformation("OmniRelay dispatcher for service {ServiceName} started", _dispatcher.ServiceName);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
        _logger.LogInformation("Stopping OmniRelay dispatcher for service {ServiceName}", _dispatcher.ServiceName);
        }

        await _dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
        _logger.LogInformation("OmniRelay dispatcher for service {ServiceName} stopped", _dispatcher.ServiceName);
        }
    }
}
