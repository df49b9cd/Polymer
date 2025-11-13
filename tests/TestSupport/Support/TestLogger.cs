using Microsoft.Extensions.Logging;

namespace OmniRelay.Tests.Support;

internal sealed class TestLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];
    private readonly AsyncLocal<ScopeState?> _currentScope = new();

    public IReadOnlyList<LogEntry> Entries => _entries;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            var snapshot = pairs as IReadOnlyList<KeyValuePair<string, object?>>
                ?? [.. pairs];

            var previous = _currentScope.Value;
            var scopeState = new ScopeState(snapshot, previous);
            _currentScope.Value = scopeState;
            return new Scope(this, previous);
        }

        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        var scopeSnapshot = ScopeState.CreateSnapshot(_currentScope.Value);
        _entries.Add(new LogEntry(logLevel, message, exception, scopeSnapshot));
    }

    private sealed class ScopeState(IReadOnlyList<KeyValuePair<string, object?>> values, ScopeState? parent)
    {
        public IReadOnlyList<KeyValuePair<string, object?>> Values { get; } = values;

        public ScopeState? Parent { get; } = parent;

        public static IReadOnlyList<KeyValuePair<string, object?>>? CreateSnapshot(ScopeState? scope)
        {
            if (scope is null)
            {
                return null;
            }

            var items = new List<KeyValuePair<string, object?>>();
            Build(scope, items);
            return items;
        }

        private static void Build(ScopeState scope, List<KeyValuePair<string, object?>> target)
        {
            if (scope.Parent is not null)
            {
                Build(scope.Parent, target);
            }

            target.AddRange(scope.Values);
        }
    }

    private sealed class Scope(TestLogger<T> logger, ScopeState? previous) : IDisposable
    {
        private readonly TestLogger<T> _logger = logger;
        private readonly ScopeState? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _logger._currentScope.Value = _previous;
        }
    }

    internal sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>>? Scope)
    {
        public LogLevel LogLevel { get; init; } = LogLevel;

        public string Message { get; init; } = Message;

        public Exception? Exception { get; init; } = Exception;

        public IReadOnlyList<KeyValuePair<string, object?>>? Scope { get; init; } = Scope;
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
