using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YARPCore.Core.Diagnostics;

namespace YARPCore.Configuration.Internal;

internal sealed class DiagnosticsRuntimeState(
    IOptionsMonitor<LoggerFilterOptions> loggerFilterOptionsMonitor,
    IOptionsMonitorCache<LoggerFilterOptions> loggerFilterOptionsCache)
    : IDiagnosticsRuntime
{
    private readonly Lock _syncRoot = new();
    private readonly LogLevel _initialMinLevel = loggerFilterOptionsMonitor.CurrentValue.MinLevel;
    private LogLevel? _overrideMinLevel;
    private double? _traceSamplingProbability;

    public LogLevel? MinimumLogLevel
    {
        get
        {
            lock (_syncRoot)
            {
                return _overrideMinLevel;
            }
        }
    }

    public double? TraceSamplingProbability
    {
        get
        {
            lock (_syncRoot)
            {
                return _traceSamplingProbability;
            }
        }
    }

    public void SetMinimumLogLevel(LogLevel? level)
    {
        lock (_syncRoot)
        {
            _overrideMinLevel = level;

            var current = loggerFilterOptionsMonitor.CurrentValue;
            var updated = new LoggerFilterOptions
            {
                MinLevel = level ?? _initialMinLevel
            };

            foreach (var rule in current.Rules)
            {
                updated.Rules.Add(rule);
            }

            loggerFilterOptionsCache.TryRemove(Options.DefaultName);
            loggerFilterOptionsCache.TryAdd(Options.DefaultName, updated);
        }
    }

    public void SetTraceSamplingProbability(double? probability)
    {
        if (probability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probability), "Sampling probability must be between 0.0 and 1.0.");
        }

        lock (_syncRoot)
        {
            _traceSamplingProbability = probability;
        }
    }
}
