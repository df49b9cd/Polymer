using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Transport;

namespace OmniRelay.Transport.Host;

/// <summary>
/// Thin hosted service that starts/stops the data-plane inbound(s).
/// </summary>
public sealed partial class DataPlaneHost : IHostedService, IAsyncDisposable
{
    private readonly IEnumerable<ITransport> _transports;
    private readonly ILogger<DataPlaneHost> _logger;

    public DataPlaneHost(IEnumerable<ITransport> transports, ILogger<DataPlaneHost> logger)
    {
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var transport in _transports)
        {
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        Log.Started(_logger, _transports.Count());
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var transport in _transports)
        {
            await transport.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        Log.Stopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var transport in _transports.OfType<IAsyncDisposable>())
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Data-plane host started {TransportCount} transport(s)")]
        public static partial void Started(ILogger logger, int transportCount);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Data-plane host stopped")]
        public static partial void Stopped(ILogger logger);
    }
}
