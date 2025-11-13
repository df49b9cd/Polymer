using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Configuration.Internal;
using Shouldly;
using Xunit;

namespace OmniRelay.Configuration.UnitTests.Configuration;

public sealed class DiagnosticsRuntimeStateTests
{
    [Fact]
    public void SetMinimumLogLevel_OverridesOptions()
    {
        var (monitor, cache) = CreateOptions();
        var runtime = new DiagnosticsRuntimeState(monitor, cache);

        runtime.SetMinimumLogLevel(LogLevel.Debug);
        runtime.MinimumLogLevel.ShouldBe(LogLevel.Debug);
        monitor.CurrentValue.MinLevel.ShouldBe(LogLevel.Debug);

        runtime.SetMinimumLogLevel(null);
        runtime.MinimumLogLevel.ShouldBeNull();
        monitor.CurrentValue.MinLevel.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public void SetTraceSamplingProbability_ValidatesRange()
    {
        var (monitor, cache) = CreateOptions();
        var runtime = new DiagnosticsRuntimeState(monitor, cache);

        runtime.SetTraceSamplingProbability(0.25);
        runtime.TraceSamplingProbability.ShouldBe(0.25);

        runtime.SetTraceSamplingProbability(null);
        runtime.TraceSamplingProbability.ShouldBeNull();

        Should.Throw<ArgumentOutOfRangeException>(() => runtime.SetTraceSamplingProbability(1.5));
        Should.Throw<ArgumentOutOfRangeException>(() => runtime.SetTraceSamplingProbability(-0.1));
    }

    private static (IOptionsMonitor<LoggerFilterOptions> Monitor, IOptionsMonitorCache<LoggerFilterOptions> Cache) CreateOptions()
    {
        var configure = new ConfigureOptions<LoggerFilterOptions>(options => options.MinLevel = LogLevel.Information);
        var factory = new OptionsFactory<LoggerFilterOptions>(
            [configure],
            []);
        var cache = new OptionsCache<LoggerFilterOptions>();
        var monitor = new OptionsMonitor<LoggerFilterOptions>(
            factory,
            [],
            cache);

        // Materialize the default value.
        _ = monitor.CurrentValue;
        return (monitor, cache);
    }
}
