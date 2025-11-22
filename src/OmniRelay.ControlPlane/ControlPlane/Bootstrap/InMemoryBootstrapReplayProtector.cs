using System.Collections.Concurrent;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>In-memory replay protection for bootstrap tokens.</summary>
public sealed class InMemoryBootstrapReplayProtector : IBootstrapReplayProtector
{
    private readonly ConcurrentDictionary<Guid, TokenUsage> _usages = new();

    public bool TryConsume(Guid tokenId, DateTimeOffset expiresAt, int? maxUses)
    {
        var now = DateTimeOffset.UtcNow;
        if (expiresAt <= now)
        {
            return false;
        }

        while (true)
        {
            if (_usages.TryGetValue(tokenId, out var usage))
            {
                if (usage.ExpiresAt <= now)
                {
                    _usages.TryRemove(tokenId, out _);
                    continue;
                }

                var nextCount = usage.Uses + 1;
                if (maxUses is not null && nextCount > maxUses.Value)
                {
                    return false;
                }

                if (_usages.TryUpdate(tokenId, new TokenUsage(nextCount, usage.ExpiresAt), usage))
                {
                    return true;
                }

                continue;
            }

            var initial = new TokenUsage(1, expiresAt);
            if (_usages.TryAdd(tokenId, initial))
            {
                return true;
            }
        }
    }

    private sealed record TokenUsage(int Uses, DateTimeOffset ExpiresAt);
}
