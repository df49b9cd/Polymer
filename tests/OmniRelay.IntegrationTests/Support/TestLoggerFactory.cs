using Microsoft.Extensions.Logging;
using Xunit;

namespace OmniRelay.IntegrationTests.Support;

internal static class TestLoggerFactory
{
    public static ILoggerFactory Create(ITestOutputHelper output) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new XunitLoggerProvider(output));
        });

    private sealed class XunitLoggerProvider(ITestOutputHelper output) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new XunitLogger(output, categoryName);

        public void Dispose()
        {
        }

        private sealed class XunitLogger(ITestOutputHelper output, string categoryName) : ILogger
        {
            private readonly object _lock = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                lock (_lock)
                {
                    output.WriteLine($"[{DateTimeOffset.UtcNow:O}] {categoryName} [{logLevel}] {formatter(state, exception)}");
                    if (exception is not null)
                    {
                        output.WriteLine(exception.ToString());
                    }
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
