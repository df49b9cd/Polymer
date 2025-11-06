using System;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public class RateLimitingMiddlewareTests
{
    private static IRequest<ReadOnlyMemory<byte>> MakeRequest(RequestMeta meta) =>
        new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

    [Fact]
    public async Task Unary_WhenNoPermits_ReturnsResourceExhausted()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var preLease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(preLease.IsAcquired);

        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc", procedure: "proc");

        UnaryOutboundDelegate next = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        var result = await middleware.InvokeAsync(MakeRequest(meta), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(result.Error!));
        preLease.Dispose();
    }

    [Fact]
    public async Task StreamOutbound_Success_ReleasesPermitOnDispose()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");
        var options = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundDelegate next = (request, callOptions, ct) =>
            ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), options, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        var blocked = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.False(blocked.IsAcquired);
        blocked.Dispose();

        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
        lease.Dispose();
    }

    [Fact]
    public async Task StreamOutbound_Failure_ReleasesPermitImmediately()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");
        var options = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundDelegate next = (request, callOptions, ct) =>
            ValueTask.FromResult(Err<IStreamCall>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "test")));

        var result = await middleware.InvokeAsync(MakeRequest(meta), options, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsFailure);

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
        lease.Dispose();
    }

    [Fact]
    public async Task StreamOutbound_CompletionWithError_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");
        var options = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundDelegate next = (request, callOptions, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(request.Meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), options, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        await result.Value.CompleteAsync(Error.Timeout(), TestContext.Current.CancellationToken);

        var intermediate = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.False(intermediate.IsAcquired);
        intermediate.Dispose();

        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
        lease.Dispose();
    }

    [Fact]
    public async Task ClientStreamOutbound_Success_ReleasesPermitOnDispose()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        ClientStreamOutboundDelegate next = (requestMeta, ct) =>
        {
            var call = new TestClientStreamCall(requestMeta);
            return ValueTask.FromResult(Ok<IClientStreamTransportCall>(call));
        };

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        var blocked = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.False(blocked.IsAcquired);
        blocked.Dispose();

        await result.Value.CompleteAsync();
        var response = await result.Value.Response;
        Assert.True(response.IsSuccess);
        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
        lease.Dispose();
    }

    [Fact]
    public async Task ClientStreamOutbound_ResponseFailure_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        ClientStreamOutboundDelegate next = (requestMeta, ct) =>
        {
            var call = new TestClientStreamCall(
                requestMeta,
                Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "test")));
            return ValueTask.FromResult(Ok<IClientStreamTransportCall>(call));
        };

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        var response = await result.Value.Response;
        Assert.True(response.IsFailure);
        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
        lease.Dispose();
    }

    [Fact]
    public async Task ClientStreamOutbound_Failure_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        ClientStreamOutboundDelegate next = (requestMeta, ct) =>
            ValueTask.FromResult(Err<IClientStreamTransportCall>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "test")));

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsFailure);

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
        lease.Dispose();
    }

    [Fact]
    public async Task DuplexOutbound_Success_ReleasesPermitOnDispose()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        DuplexOutboundDelegate next = (request, ct) =>
            ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        var blocked = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.False(blocked.IsAcquired);
        blocked.Dispose();

        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
        lease.Dispose();
    }

    [Fact]
    public async Task DuplexOutbound_CompletionFailure_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        DuplexOutboundDelegate next = (request, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(request.Meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        await result.Value.CompleteRequestsAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.CompleteResponsesAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
        lease.Dispose();
    }

    [Fact]
    public async Task LimiterSelectorReturningNull_SkipsRateLimiting()
    {
        var options = new RateLimitingOptions
        {
            Limiter = null,
            LimiterSelector = _ => null
        };

        var middleware = new RateLimitingMiddleware(options);
        var meta = new RequestMeta(service: "svc");
        var invoked = false;

        UnaryOutboundDelegate next = (req, ct) =>
        {
            invoked = true;
            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        };

        var result = await middleware.InvokeAsync(MakeRequest(meta), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsSuccess);
        Assert.True(invoked);
    }

    private sealed class TestClientStreamCall : IClientStreamTransportCall
    {
        private readonly TaskCompletionSource<Result<Response<ReadOnlyMemory<byte>>>> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestClientStreamCall(RequestMeta meta, Result<Response<ReadOnlyMemory<byte>>>? responseOverride = null)
        {
            RequestMeta = meta;
            if (responseOverride is { } overridden)
            {
                _tcs.TrySetResult(overridden);
            }
        }

        public RequestMeta RequestMeta { get; }
        public ResponseMeta ResponseMeta { get; } = new();
        public Task<Result<Response<ReadOnlyMemory<byte>>>> Response => _tcs.Task;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (!_tcs.Task.IsCompleted)
            {
                _tcs.TrySetResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_tcs.Task.IsCompleted)
            {
                _tcs.TrySetResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }

            return ValueTask.CompletedTask;
        }
    }
}
