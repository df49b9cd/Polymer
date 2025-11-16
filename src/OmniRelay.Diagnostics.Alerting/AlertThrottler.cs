using System.Collections.Concurrent;

namespace OmniRelay.Diagnostics.Alerting;

internal sealed class AlertThrottler
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldThrottle(string key, TimeSpan cooldown, DateTimeOffset now)
    {
        if (cooldown <= TimeSpan.Zero)
        {
            return false;
        }

        var expires = _cooldowns.GetOrAdd(key, static _ => DateTimeOffset.MinValue);
        if (expires > now)
        {
            return true;
        }

        _cooldowns[key] = now + cooldown;
        return false;
    }
}
