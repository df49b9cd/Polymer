using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public class RpcLoggingMiddlewareTests
{
    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly Func<LogLevel, bool> _isEnabled;

        public TestLogger(Func<LogLevel, bool>? isEnabled = null)
        {
            _isEnabled = isEnabled ?? (_ => true);
        }

        public ConcurrentQueue<(LogLevel level, string message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Noop();
        public bool IsEnabled(LogLevel logLevel) => _isEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task LogsSuccess_OnUnaryOutbound()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var mw = new RpcLoggingMiddleware(logger);
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http", encoding: "json");
        UnaryOutboundDelegate next = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta(encoding: "json"))));

        var res = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsSuccess);
        Assert.Contains(logger.Entries, e => e.level >= LogLevel.Information && e.message.Contains("outbound unary"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task LogsFailure_OnUnaryOutbound()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var mw = new RpcLoggingMiddleware(logger);
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundDelegate next = (req, ct) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));

        var res = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsFailure);
        Assert.Contains(logger.Entries, e => e.level >= LogLevel.Warning && e.message.Contains("failed"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SkipsLogging_WhenPredicatePrevents()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var options = new RpcLoggingOptions
        {
            ShouldLogRequest = _ => false
        };
        var mw = new RpcLoggingMiddleware(logger, options);
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");

        UnaryOutboundDelegate next = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        var result = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsSuccess);
        Assert.Empty(logger.Entries);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task LogsException_WhenNextThrows()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var mw = new RpcLoggingMiddleware(logger);
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");

        UnaryOutboundDelegate next = (req, ct) => throw new ApplicationException("boom");

        var exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
            await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next));

        Assert.Equal("boom", exception.Message);
        Assert.Contains(logger.Entries, entry => entry.level >= LogLevel.Warning && entry.message.Contains("threw"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task LogsFailure_WhenErrorPredicateAllows()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var options = new RpcLoggingOptions
        {
            ShouldLogRequest = _ => false,
            ShouldLogError = _ => true
        };
        var mw = new RpcLoggingMiddleware(logger, options);
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "fail", transport: "http");

        UnaryOutboundDelegate next = (req, ct) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));

        var result = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsFailure);
        Assert.Contains(logger.Entries, entry => entry.level >= LogLevel.Warning && entry.message.Contains("fail"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ExceptionWithoutLogging_WhenDisabled()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>(_ => false);
        var options = new RpcLoggingOptions
        {
            ShouldLogRequest = _ => false,
            FailureLogLevel = LogLevel.Warning
        };
        var mw = new RpcLoggingMiddleware(logger, options);
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundDelegate next = (req, ct) => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next));

        Assert.Empty(logger.Entries);
    }
}
