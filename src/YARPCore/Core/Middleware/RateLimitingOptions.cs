using System.Threading.RateLimiting;

namespace YARPCore.Core.Middleware;

public sealed class RateLimitingOptions
{
    public RateLimiter? Limiter { get; init; }

    public Func<RequestMeta, RateLimiter?>? LimiterSelector { get; init; }
}
