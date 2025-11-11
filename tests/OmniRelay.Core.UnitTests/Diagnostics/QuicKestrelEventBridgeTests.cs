using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Diagnostics;
using Xunit;

namespace OmniRelay.Core.UnitTests.Diagnostics;

public class QuicKestrelEventBridgeTests
{
    private sealed class TestLogger : ILogger<QuicKestrelEventBridge>
    {
        private readonly LogLevel _minLevel;
        private readonly AsyncLocal<object?> _currentScope = new();

        public ConcurrentQueue<(LogLevel level, string message, object? scope)> Entries { get; } = new();

        public TestLogger(LogLevel minLevel = LogLevel.Debug)
        {
            _minLevel = minLevel;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var prior = _currentScope.Value;
            _currentScope.Value = state;
            return new Scope(() => _currentScope.Value = prior);
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var msg = formatter(state, exception);
            Entries.Enqueue((logLevel, msg, _currentScope.Value));
        }

        private sealed class Scope(Action onDispose) : IDisposable
        {
            private readonly Action _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }

    [EventSource(Name = "Private.InternalDiagnostics.System.Net.Quic")]
    private sealed class MsQuicTestEventSource : EventSource
    {
        public static MsQuicTestEventSource Create() => new MsQuicTestEventSource();

        [Event(1, Level = EventLevel.Informational)]
        public void HandshakeError(string reason) => WriteEvent(1, reason);

        [Event(2, Level = EventLevel.Informational)]
        public void PathValidated(string path) => WriteEvent(2, path);
    }

    [EventSource(Name = "Microsoft-AspNetCore-Server-Kestrel")]
    private sealed class KestrelTestEventSource : EventSource
    {
        public static KestrelTestEventSource Create() => new KestrelTestEventSource();

        [Event(10, Level = EventLevel.Informational)]
        public void Http3Connection(string info) => WriteEvent(10, info);
    }

    private static async Task<(TestLogger logger, QuicKestrelEventBridge bridge)> CreateBridgeAsync(LogLevel minLevel = LogLevel.Debug)
    {
        var logger = new TestLogger(minLevel);
        var bridge = new QuicKestrelEventBridge(logger);
        // small yield to ensure EventListener hooks up
        await Task.Yield();
        return (logger, bridge);
    }

    private static async Task AssertEventuallyAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < until)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(predicate());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Logs_Warning_On_HandshakeFailure()
    {
        var (logger, bridge) = await CreateBridgeAsync();
        using var src = MsQuicTestEventSource.Create();

        src.HandshakeError("handshake failure: cert error");

        await AssertEventuallyAsync(() => !logger.Entries.IsEmpty);
        Assert.True(logger.Entries.TryDequeue(out var entry));
        Assert.Equal(LogLevel.Warning, entry.level);
        Assert.Contains("handshake_failure", entry.message);
        Assert.NotNull(entry.scope);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Logs_Information_On_Migration()
    {
        var (logger, bridge) = await CreateBridgeAsync();
        using var src = MsQuicTestEventSource.Create();

        src.PathValidated("path_validated: new path");

        await AssertEventuallyAsync(() => !logger.Entries.IsEmpty);
        Assert.True(logger.Entries.TryDequeue(out var entry));
        Assert.Equal(LogLevel.Information, entry.level);
        Assert.Contains("migration", entry.message);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Logs_Debug_On_Kestrel_Http3_When_Debug_Enabled()
    {
        var (logger, bridge) = await CreateBridgeAsync(LogLevel.Debug);
        using var src = KestrelTestEventSource.Create();

        src.Http3Connection("started");

        await AssertEventuallyAsync(() => !logger.Entries.IsEmpty);
        Assert.True(logger.Entries.TryDequeue(out var entry));
        Assert.Equal(LogLevel.Debug, entry.level);
        Assert.Contains("http3", entry.message);
    }
}
