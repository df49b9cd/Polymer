using System.Threading.RateLimiting;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Configuration for RPC rate limiting middleware, providing a limiter or per-request selector.
/// </summary>
public sealed class RateLimitingOptions
{
    /// <summary>Gets or sets the default limiter used when no selector is provided.</summary>
    public RateLimiter? Limiter { get; init; }

    /// <summary>Optional selector to choose a limiter per-request based on metadata.</summary>
    public Func<RequestMeta, RateLimiter?>? LimiterSelector { get; init; }
}
