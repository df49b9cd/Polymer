using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using YARPCore.Configuration.Internal;

namespace YARPCore.Tests.Configuration;

public sealed class DiagnosticsRuntimeStateTests
{
    [Fact]
    public void SetMinimumLogLevel_OverridesOptions()
    {
        var (monitor, cache) = CreateOptions();
        var runtime = new DiagnosticsRuntimeState(monitor, cache);

        runtime.SetMinimumLogLevel(LogLevel.Debug);
        Assert.Equal(LogLevel.Debug, runtime.MinimumLogLevel);
        Assert.Equal(LogLevel.Debug, monitor.CurrentValue.MinLevel);

        runtime.SetMinimumLogLevel(null);
        Assert.Null(runtime.MinimumLogLevel);
        Assert.Equal(LogLevel.Information, monitor.CurrentValue.MinLevel);
    }

    [Fact]
    public void SetTraceSamplingProbability_ValidatesRange()
    {
        var (monitor, cache) = CreateOptions();
        var runtime = new DiagnosticsRuntimeState(monitor, cache);

        runtime.SetTraceSamplingProbability(0.25);
        Assert.Equal(0.25, runtime.TraceSamplingProbability);

        runtime.SetTraceSamplingProbability(null);
        Assert.Null(runtime.TraceSamplingProbability);

        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.SetTraceSamplingProbability(1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.SetTraceSamplingProbability(-0.1));
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
