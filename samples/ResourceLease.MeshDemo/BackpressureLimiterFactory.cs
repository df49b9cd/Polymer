using System.Threading.RateLimiting;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal static class BackpressureLimiterFactory
{
    public static RateLimiter Create(int permitLimit)
    {
        return new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Max(1, permitLimit),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }
}
