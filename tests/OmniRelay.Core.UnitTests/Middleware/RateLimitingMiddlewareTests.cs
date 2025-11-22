using System.Threading.Channels;
using System.Threading.RateLimiting;
using Hugo;
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

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Unary_WhenNoPermits_ReturnsResourceExhausted()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var preLease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        preLease.IsAcquired.ShouldBeTrue();

        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc", procedure: "proc");

        UnaryOutboundHandler next = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        var result = await middleware.InvokeAsync(MakeRequest(meta), TestContext.Current.CancellationToken, next);

        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.ResourceExhausted);
        preLease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StreamOutbound_Success_ReleasesPermitOnDispose()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");
        var options = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundHandler next = (request, callOptions, ct) =>
            ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), options, TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        var blocked = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        blocked.IsAcquired.ShouldBeFalse();
        blocked.Dispose();

        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StreamOutbound_Failure_ReleasesPermitImmediately()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");
        var options = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundHandler next = (request, callOptions, ct) =>
            ValueTask.FromResult(Err<IStreamCall>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "test")));

        var result = await middleware.InvokeAsync(MakeRequest(meta), options, TestContext.Current.CancellationToken, next);
        result.IsFailure.ShouldBeTrue();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StreamOutbound_CompletionWithError_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");
        var options = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundHandler next = (request, callOptions, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(request.Meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), options, TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        await result.Value.CompleteAsync(Error.Timeout(), TestContext.Current.CancellationToken);

        var intermediate = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        intermediate.IsAcquired.ShouldBeFalse();
        intermediate.Dispose();

        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ClientStreamOutbound_Success_ReleasesPermitOnDispose()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        ClientStreamOutboundHandler next = (requestMeta, ct) =>
        {
            var call = new TestClientStreamCall(requestMeta);
            return ValueTask.FromResult(Ok<IClientStreamTransportCall>(call));
        };

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        var blocked = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        blocked.IsAcquired.ShouldBeFalse();
        blocked.Dispose();

        await result.Value.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        var response = await result.Value.Response;
        response.IsSuccess.ShouldBeTrue();
        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ClientStreamOutbound_ResponseFailure_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        ClientStreamOutboundHandler next = (requestMeta, ct) =>
        {
            var call = new TestClientStreamCall(
                requestMeta,
                Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "test")));
            return ValueTask.FromResult(Ok<IClientStreamTransportCall>(call));
        };

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        var response = await result.Value.Response;
        response.IsFailure.ShouldBeTrue();
        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ClientStreamOutbound_Failure_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        ClientStreamOutboundHandler next = (requestMeta, ct) =>
            ValueTask.FromResult(Err<IClientStreamTransportCall>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "test")));

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        result.IsFailure.ShouldBeTrue();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask DuplexOutbound_Success_ReleasesPermitOnDispose()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        DuplexOutboundHandler next = (request, ct) =>
            ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        var blocked = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        blocked.IsAcquired.ShouldBeFalse();
        blocked.Dispose();

        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask DuplexOutbound_CompletionFailure_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        DuplexOutboundHandler next = (request, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(request.Meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        await result.Value.CompleteRequestsAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.CompleteResponsesAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.DisposeAsync();

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask LimiterSelectorReturningNull_SkipsRateLimiting()
    {
        var options = new RateLimitingOptions
        {
            Limiter = null,
            LimiterSelector = _ => null
        };

        var middleware = new RateLimitingMiddleware(options);
        var meta = new RequestMeta(service: "svc");
        var invoked = false;

        UnaryOutboundHandler next = (req, ct) =>
        {
            invoked = true;
            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        };

        var result = await middleware.InvokeAsync(MakeRequest(meta), TestContext.Current.CancellationToken, next);

        result.IsSuccess.ShouldBeTrue();
        invoked.ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StreamOutbound_DisposeThrows_ReleasesPermit()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new RateLimitingMiddleware(new RateLimitingOptions { Limiter = limiter });
        var meta = new RequestMeta(service: "svc");

        StreamOutboundHandler next = (request, options, ct) =>
            ValueTask.FromResult(Ok<IStreamCall>(new ThrowingStreamCall(request.Meta)));

        var result = await middleware.InvokeAsync(MakeRequest(meta), new StreamCallOptions(StreamDirection.Server), TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        await Should.ThrowAsync<InvalidOperationException>(() => result.Value.DisposeAsync().AsTask());

        var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        lease.IsAcquired.ShouldBeTrue();
        lease.Dispose();
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
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response => new(_tcs.Task);

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

    private sealed class ThrowingStreamCall(RequestMeta meta) : IStreamCall
    {
        private readonly Channel<ReadOnlyMemory<byte>> _channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public StreamDirection Direction => StreamDirection.Server;
        public RequestMeta RequestMeta { get; } = meta;
        public ResponseMeta ResponseMeta { get; } = new();
        public StreamCallContext Context { get; } = new(StreamDirection.Server);
        public ChannelWriter<ReadOnlyMemory<byte>> Requests => _channel.Writer;
        public ChannelReader<ReadOnlyMemory<byte>> Responses => _channel.Reader;

        public ValueTask CompleteAsync(Error? fault = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("dispose failure"));
    }
}
