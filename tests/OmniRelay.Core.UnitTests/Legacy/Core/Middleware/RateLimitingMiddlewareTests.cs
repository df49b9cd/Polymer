using System.Threading.RateLimiting;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Core.Middleware;

public sealed class RateLimitingMiddlewareTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryOutbound_ExceedsLimit_ReturnsResourceExhausted()
    {
        var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var lease = await limiter.AcquireAsync(1, CancellationToken.None);
        lease.IsAcquired.ShouldBeTrue();

        var result = await middleware.InvokeAsync(request, CancellationToken.None, (UnaryOutboundHandler)((req, token) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)))));

        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);

        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryOutbound_ReleasesPermitAfterSuccess()
    {
        var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var result1 = await middleware.InvokeAsync(request, CancellationToken.None, (UnaryOutboundHandler)((req, token) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)))));
        result1.IsSuccess.ShouldBeTrue();

        var result2 = await middleware.InvokeAsync(request, CancellationToken.None, (UnaryOutboundHandler)((req, token) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)))));
        result2.IsSuccess.ShouldBeTrue();
    }
}
