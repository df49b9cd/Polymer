using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using Shouldly;
using Xunit;

namespace OmniRelay.Configuration.UnitTests.Configuration;

public sealed class DispatcherHostedServiceTests
{
    [Fact]
    public async Task StartAndStopAsync_LogLifecycleAndDriveDispatcher()
    {
        var (dispatcher, lifecycle) = CreateDispatcher();
        var logger = new TestLogger<DispatcherHostedService>();
        var hostedService = new DispatcherHostedService(dispatcher, logger);

        await hostedService.StartAsync(CancellationToken.None);
        lifecycle.StartCalls.ShouldBe(1);
        logger.Entries.ShouldContain(entry => entry.EventId.Id == 1 && entry.Message.Contains("Starting"));
        logger.Entries.ShouldContain(entry => entry.EventId.Id == 2 && entry.Message.Contains("started"));

        await hostedService.StopAsync(CancellationToken.None);
        lifecycle.StopCalls.ShouldBe(1);
        logger.Entries.ShouldContain(entry => entry.EventId.Id == 3 && entry.Message.Contains("Stopping"));
        logger.Entries.ShouldContain(entry => entry.EventId.Id == 4 && entry.Message.Contains("stopped"));
    }

    [Fact]
    public async Task StartAsync_WhenDispatcherFails_ThrowsResultException()
    {
        var (dispatcher, lifecycle) = CreateDispatcher(lifecycle => lifecycle.FailStart = true);
        var logger = new TestLogger<DispatcherHostedService>();
        var hostedService = new DispatcherHostedService(dispatcher, logger);

        await Should.ThrowAsync<ResultException>(() => hostedService.StartAsync(CancellationToken.None));
        lifecycle.StartCalls.ShouldBe(1);
        logger.Entries.ShouldContain(entry => entry.EventId.Id == 1);
        logger.Entries.ShouldNotContain(entry => entry.EventId.Id == 2);
    }

    [Fact]
    public async Task StopAsync_WhenDispatcherFails_ThrowsResultException()
    {
        var (dispatcher, lifecycle) = CreateDispatcher(lifecycle => lifecycle.FailStop = true);
        var logger = new TestLogger<DispatcherHostedService>();
        var hostedService = new DispatcherHostedService(dispatcher, logger);

        await hostedService.StartAsync(CancellationToken.None);
        lifecycle.StartCalls.ShouldBe(1);

        await Should.ThrowAsync<ResultException>(() => hostedService.StopAsync(CancellationToken.None));
        lifecycle.StopCalls.ShouldBe(1);
        logger.Entries.ShouldContain(entry => entry.EventId.Id == 3);
        logger.Entries.ShouldNotContain(entry => entry.EventId.Id == 4);
    }

    private static (Dispatcher.Dispatcher Dispatcher, TestLifecycle Lifecycle) CreateDispatcher(
        Action<TestLifecycle>? configure = null)
    {
        var lifecycle = new TestLifecycle();
        configure?.Invoke(lifecycle);

        var options = new DispatcherOptions("tests-service");
        options.AddLifecycle("test-component", lifecycle);

        return (new Dispatcher.Dispatcher(options), lifecycle);
    }

    private sealed class TestLifecycle : ILifecycle
    {
        public bool FailStart { get; set; }
        public bool FailStop { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            if (FailStart)
            {
                throw new InvalidOperationException("start failure");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            if (FailStop)
            {
                throw new InvalidOperationException("stop failure");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
