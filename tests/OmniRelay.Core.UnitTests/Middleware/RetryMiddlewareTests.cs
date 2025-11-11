using System;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Hugo.Policies;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public class RetryMiddlewareTests
{
    private static IRequest<ReadOnlyMemory<byte>> MakeReq(RequestMeta meta) => new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ShouldRetryRequest_False_DoesNotRetry()
    {
        var options = new RetryOptions
        {
            ShouldRetryRequest = _ => false
        };
        var mw = new RetryMiddleware(options);

        var attempts = 0;
        UnaryOutboundHandler next = (req, ct) =>
        {
            attempts++;
            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(Error.Timeout()));
        };

        var res = await mw.InvokeAsync(MakeReq(new RequestMeta(service: "svc")), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsFailure);
        Assert.Equal(1, attempts);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ShouldRetryError_False_DoesNotRetry()
    {
        var options = new RetryOptions
        {
            ShouldRetryError = _ => false
        };
        var mw = new RetryMiddleware(options);

        var attempts = 0;
        UnaryOutboundHandler next = (req, ct) =>
        {
            attempts++;
            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(Error.Timeout()));
        };

        var res = await mw.InvokeAsync(MakeReq(new RequestMeta(service: "svc")), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsFailure);
        Assert.Equal(1, attempts);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RetriesUntilSuccess_WhenPolicyAllows()
    {
        var options = new RetryOptions
        {
            Policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.Zero))
        };
        var middleware = new RetryMiddleware(options);

        var attempts = 0;
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "retry", transport: "test");
        UnaryOutboundHandler next = (req, ct) =>
        {
            attempts++;
            if (attempts < 2)
            {
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            }

            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        };

        var result = await middleware.InvokeAsync(MakeReq(new RequestMeta(service: "svc")), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, attempts);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task NonRetryableError_ReturnsImmediately()
    {
        var options = new RetryOptions
        {
            Policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.Zero))
        };
        var middleware = new RetryMiddleware(options);

        var attempts = 0;
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.InvalidArgument, "nope", transport: "test");
        UnaryOutboundHandler next = (req, ct) =>
        {
            attempts++;
            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
        };

        var result = await middleware.InvokeAsync(MakeReq(new RequestMeta(service: "svc")), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsFailure);
        Assert.Equal(1, attempts);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PolicySelector_OverridesDefaultPolicy()
    {
        var overridePolicy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 2, delay: TimeSpan.Zero));
        var options = new RetryOptions
        {
            Policy = ResultExecutionPolicy.None,
            PolicySelector = meta => overridePolicy
        };
        var middleware = new RetryMiddleware(options);

        var attempts = 0;
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "retry", transport: "test");
        UnaryOutboundHandler next = (req, ct) =>
        {
            attempts++;
            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
        };

        var result = await middleware.InvokeAsync(MakeReq(new RequestMeta(service: "svc")), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsFailure);
        Assert.Equal(2, attempts);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ShouldRetryError_OverridesDefaultRetryability()
    {
        var options = new RetryOptions
        {
            ShouldRetryError = _ => true,
            Policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.Zero))
        };
        var middleware = new RetryMiddleware(options);

        var attempts = 0;
        var nonRetryable = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.InvalidArgument, "nope", transport: "test");

        UnaryOutboundHandler next = (req, ct) =>
        {
            attempts++;
            if (attempts < 3)
            {
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(nonRetryable));
            }

            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        };

        var result = await middleware.InvokeAsync(MakeReq(new RequestMeta(service: "svc")), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, attempts);
    }
}
