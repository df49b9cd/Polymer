using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using OmniRelay.Core.Diagnostics;
using OpenTelemetry.Trace;
using Xunit;

namespace OmniRelay.Core.UnitTests.Diagnostics;

public class DiagnosticsRuntimeSamplerTests
{
    private static SamplingParameters MakeParams(ActivityTraceFlags parentFlags = default, IEnumerable<ActivityLink>? links = null)
    {
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), parentFlags, traceState: null, isRemote: true);
        return new SamplingParameters(
            parent,
            ActivityTraceId.CreateRandom(),
            "op",
            ActivityKind.Server,
            null,
            links);
    }

    private sealed class TestRuntime : IDiagnosticsRuntime
    {
        public Microsoft.Extensions.Logging.LogLevel? MinimumLogLevel { get; private set; }
        public double? TraceSamplingProbability { get; private set; }
        public void SetMinimumLogLevel(Microsoft.Extensions.Logging.LogLevel? level) => MinimumLogLevel = level;
        public void SetTraceSamplingProbability(double? probability) => TraceSamplingProbability = probability;
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void NullRuntime_UsesFallback()
    {
        var fallback = new AlwaysOffSampler();
        var sampler = new DiagnosticsRuntimeSampler(null, fallback);
        var result = sampler.ShouldSample(MakeParams());
        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void NullProbability_UsesFallback()
    {
        var runtime = new TestRuntime();
        // probability remains null
        var fallback = new AlwaysOnSampler();
        var sampler = new DiagnosticsRuntimeSampler(runtime, fallback);
        var result = sampler.ShouldSample(MakeParams());
        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ProbabilityZero_Drops_UnlessRecordedParentOrLink()
    {
        var runtime = new TestRuntime();
        runtime.SetTraceSamplingProbability(0.0);
        var sampler = new DiagnosticsRuntimeSampler(runtime, new AlwaysOnSampler());

        // no recorded parent or link -> drop
        var result1 = sampler.ShouldSample(MakeParams());
        Assert.Equal(SamplingDecision.Drop, result1.Decision);

        // recorded parent -> sample
        var result2 = sampler.ShouldSample(MakeParams(ActivityTraceFlags.Recorded));
        Assert.Equal(SamplingDecision.RecordAndSample, result2.Decision);

        // recorded link -> sample
        var linkCtx = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, null, true);
        var links = new[] { new ActivityLink(linkCtx) };
        var result3 = sampler.ShouldSample(MakeParams(default, links));
        Assert.Equal(SamplingDecision.RecordAndSample, result3.Decision);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ProbabilityOneOrMore_UsesFallback()
    {
        var runtime = new TestRuntime();
        runtime.SetTraceSamplingProbability(1.0);
        var fallback = new AlwaysOffSampler();
        var sampler = new DiagnosticsRuntimeSampler(runtime, fallback);
        var result = sampler.ShouldSample(MakeParams());
        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RatioSampler_IsCached_PerProbability()
    {
        var runtime = new TestRuntime();
        var sampler = new DiagnosticsRuntimeSampler(runtime, new AlwaysOffSampler());

        runtime.SetTraceSamplingProbability(0.25);
        _ = sampler.ShouldSample(MakeParams());
        var cache1 = GetCachedSampler(sampler);
        Assert.NotNull(cache1);
        Assert.Equal(0.25, GetProbability(cache1!));
        var samplerObj1 = GetInnerSampler(cache1!);

        runtime.SetTraceSamplingProbability(0.50);
        _ = sampler.ShouldSample(MakeParams());
        var cache2 = GetCachedSampler(sampler);
        Assert.NotNull(cache2);
        Assert.Equal(0.50, GetProbability(cache2!));
        var samplerObj2 = GetInnerSampler(cache2!);

        Assert.NotSame(samplerObj1, samplerObj2);
    }

    private static object? GetCachedSampler(DiagnosticsRuntimeSampler sampler)
    {
        var field = typeof(DiagnosticsRuntimeSampler).GetField("_cachedRatioSampler", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(sampler);
    }

    private static double GetProbability(object cache)
    {
        var prop = cache.GetType().GetProperty("Probability", BindingFlags.Instance | BindingFlags.Public);
        return (double)(prop!.GetValue(cache) ?? 0d);
    }

    private static object GetInnerSampler(object cache)
    {
        var prop = cache.GetType().GetProperty("Sampler", BindingFlags.Instance | BindingFlags.Public);
        return prop!.GetValue(cache)!;
    }
}
