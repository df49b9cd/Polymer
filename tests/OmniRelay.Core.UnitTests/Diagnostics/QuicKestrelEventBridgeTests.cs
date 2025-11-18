using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Diagnostics;
using Xunit;

namespace OmniRelay.Core.UnitTests.Diagnostics;

[Collection(nameof(QuicDiagnosticsCollection))]
public class QuicKestrelEventBridgeTests
{
    private record LogEntry(LogLevel Level, string Message, object? Scope);

    private sealed class TestLogger(LogLevel minLevel = LogLevel.Debug) : ILogger<QuicKestrelEventBridge>
    {
        private readonly LogLevel _minLevel = minLevel;
        private readonly AsyncLocal<object?> _currentScope = new();
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

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
            Entries.Enqueue(new LogEntry(logLevel, msg, _currentScope.Value));
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
    }
}
