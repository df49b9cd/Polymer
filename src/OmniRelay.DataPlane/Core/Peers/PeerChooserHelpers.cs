namespace OmniRelay.Core.Peers;

/// <summary>
/// Shared utilities for peer choosers to compute wait deadlines and delays.
/// </summary>
internal static class PeerChooserHelpers
{
    private static readonly TimeSpan DefaultWaitSlice = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Resolves the effective deadline for peer acquisition using the request metadata.
    /// Prefers an absolute deadline when provided and otherwise derives it from the time-to-live.
    /// </summary>
    public static DateTimeOffset? ResolveDeadline(RequestMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        DateTimeOffset? resolved = null;

        if (meta.Deadline is { } absolute)
        {
            resolved = absolute.ToUniversalTime();
        }

        if (meta.TimeToLive is { } ttl && ttl > TimeSpan.Zero)
        {
            var ttlDeadline = DateTimeOffset.UtcNow.Add(ttl);
            resolved = resolved.HasValue && resolved.Value <= ttlDeadline ? resolved : ttlDeadline;
        }

        return resolved;
    }

    /// <summary>
    /// Determines whether the supplied deadline has elapsed.
    /// </summary>
    public static bool HasDeadlineElapsed(DateTimeOffset? deadline) =>
        deadline is { } value && value <= DateTimeOffset.UtcNow;

    /// <summary>
    /// Computes the wait delay respecting the provided deadline or defaults when none is supplied.
    /// </summary>
    public static TimeSpan GetWaitDelay(DateTimeOffset? deadline)
    {
        if (deadline is not { } value)
        {
            return DefaultWaitSlice;
        }

        var remaining = value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining <= DefaultWaitSlice ? remaining : DefaultWaitSlice;
    }
}
