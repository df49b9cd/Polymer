using Hugo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OmniRelay.Configuration;

/// <summary>
/// Hosted service that starts and stops the OmniRelay dispatcher and logs lifecycle events.
/// </summary>
internal sealed class DispatcherHostedService(Dispatcher.Dispatcher dispatcher, ILogger<DispatcherHostedService>? logger = null) : IHostedService
{
    private readonly Dispatcher.Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly ILogger<DispatcherHostedService> _logger = logger ?? NullLogger<DispatcherHostedService>.Instance;

    /// <summary>Starts the dispatcher.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            DispatcherHostedServiceLog.Starting(_logger, _dispatcher.ServiceName);
        }

        var startResult = await _dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        if (startResult.IsFailure)
        {
            throw new ResultException(startResult.Error!);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            DispatcherHostedServiceLog.Started(_logger, _dispatcher.ServiceName);
        }
    }

    /// <summary>Stops the dispatcher.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            DispatcherHostedServiceLog.Stopping(_logger, _dispatcher.ServiceName);
        }

        var stopResult = await _dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);
        if (stopResult.IsFailure)
        {
            throw new ResultException(stopResult.Error!);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            DispatcherHostedServiceLog.Stopped(_logger, _dispatcher.ServiceName);
        }
    }
}

internal static partial class DispatcherHostedServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting OmniRelay dispatcher for service {ServiceName}")]
    public static partial void Starting(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OmniRelay dispatcher for service {ServiceName} started")]
    public static partial void Started(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Stopping OmniRelay dispatcher for service {ServiceName}")]
    public static partial void Stopping(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "OmniRelay dispatcher for service {ServiceName} stopped")]
    public static partial void Stopped(ILogger logger, string serviceName);
}
