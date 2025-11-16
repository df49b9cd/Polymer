using Microsoft.Extensions.Logging;

namespace OmniRelay.Diagnostics.Alerting;

/// <summary>Dispatches alerts to configured channels with throttling.</summary>
public sealed partial class AlertPublisher : IAlertPublisher
{
    private readonly IReadOnlyList<IAlertChannel> _channels;
    private readonly IReadOnlyDictionary<string, TimeSpan> _cooldowns;
    private readonly AlertThrottler _throttler = new();
    private readonly ILogger<AlertPublisher> _logger;
    private readonly TimeSpan _defaultCooldown;

    public AlertPublisher(
        IReadOnlyList<IAlertChannel> channels,
        IReadOnlyDictionary<string, TimeSpan> cooldowns,
        TimeSpan defaultCooldown,
        ILogger<AlertPublisher> logger)
    {
        _channels = channels;
        _cooldowns = cooldowns;
        _defaultCooldown = defaultCooldown;
        _logger = logger;
    }

    public async ValueTask PublishAsync(AlertEvent alert, CancellationToken cancellationToken = default)
    {
        if (_channels.Count == 0)
        {
            return;
        }

        foreach (var channel in _channels)
        {
            var cooldown = _cooldowns.TryGetValue(channel.Name, out var perChannel) ? perChannel : _defaultCooldown;
            var key = $"{channel.Name}:{alert.Name}:{alert.Severity}";
            if (_throttler.ShouldThrottle(key, cooldown, DateTimeOffset.UtcNow))
            {
                continue;
            }

            try
            {
                await channel.SendAsync(alert, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.AlertChannelFailed(_logger, channel.Name, alert.Name, ex);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Alert channel {Channel} failed to emit event {Alert}.")]
        public static partial void AlertChannelFailed(ILogger logger, string channel, string alert, Exception exception);
    }
}
