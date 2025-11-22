using Hugo.Policies;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Core.Middleware;

public sealed class RetryMiddlewareTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryOutbound_RetriesUntilSuccess()
    {
        var options = new RetryOptions
        {
            Policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.Zero))
        };
        var middleware = new RetryMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "unavailable", transport: "grpc");

        var attempt = 0;
        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) =>
            {
                attempt++;
                if (attempt < 2)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        result.IsSuccess.ShouldBeTrue();
        attempt.ShouldBe(2);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryOutbound_NonRetryableError_ReturnsImmediately()
    {
        var options = new RetryOptions
        {
            Policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.Zero))
        };
        var middleware = new RetryMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.InvalidArgument, "invalid", transport: "grpc");

        var attempt = 0;
        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) =>
            {
                attempt++;
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            }));

        result.IsFailure.ShouldBeTrue();
        attempt.ShouldBe(1);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ShouldRetryRequestFalse_SkipsRetries()
    {
        var options = new RetryOptions
        {
            Policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.Zero)),
            ShouldRetryRequest = _ => false
        };
        var middleware = new RetryMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "oops", transport: "grpc");

        var attempt = 0;
        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) =>
            {
                attempt++;
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            }));

        result.IsFailure.ShouldBeTrue();
        attempt.ShouldBe(1);
    }
}
