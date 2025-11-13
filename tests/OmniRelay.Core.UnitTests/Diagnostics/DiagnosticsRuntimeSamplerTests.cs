using System.Diagnostics;
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
        result.Decision.ShouldBe(SamplingDecision.Drop);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void NullProbability_UsesFallback()
    {
        var runtime = new TestRuntime();
        // probability remains null
        var fallback = new AlwaysOnSampler();
        var sampler = new DiagnosticsRuntimeSampler(runtime, fallback);
        var result = sampler.ShouldSample(MakeParams());
        result.Decision.ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ProbabilityZero_Drops_UnlessRecordedParentOrLink()
    {
        var runtime = new TestRuntime();
        runtime.SetTraceSamplingProbability(0.0);
        var sampler = new DiagnosticsRuntimeSampler(runtime, new AlwaysOnSampler());

        // no recorded parent or link -> drop
        var result1 = sampler.ShouldSample(MakeParams());
        result1.Decision.ShouldBe(SamplingDecision.Drop);

        // recorded parent -> sample
        var result2 = sampler.ShouldSample(MakeParams(ActivityTraceFlags.Recorded));
        result2.Decision.ShouldBe(SamplingDecision.RecordAndSample);

        // recorded link -> sample
        var linkCtx = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, null, true);
        var links = new[] { new ActivityLink(linkCtx) };
        var result3 = sampler.ShouldSample(MakeParams(default, links));
        result3.Decision.ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ProbabilityOneOrMore_UsesFallback()
    {
        var runtime = new TestRuntime();
        runtime.SetTraceSamplingProbability(1.0);
        var fallback = new AlwaysOffSampler();
        var sampler = new DiagnosticsRuntimeSampler(runtime, fallback);
        var result = sampler.ShouldSample(MakeParams());
        result.Decision.ShouldBe(SamplingDecision.Drop);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RatioSampler_IsCached_PerProbability()
    {
        var runtime = new TestRuntime();
        var sampler = new DiagnosticsRuntimeSampler(runtime, new AlwaysOffSampler());

        runtime.SetTraceSamplingProbability(0.25);
        _ = sampler.ShouldSample(MakeParams());
        var cache1 = GetCachedSampler(sampler);
        cache1.ShouldNotBeNull();
        GetProbability(cache1!).ShouldBe(0.25);
        var samplerObj1 = GetInnerSampler(cache1!);

        runtime.SetTraceSamplingProbability(0.50);
        _ = sampler.ShouldSample(MakeParams());
        var cache2 = GetCachedSampler(sampler);
        cache2.ShouldNotBeNull();
        GetProbability(cache2!).ShouldBe(0.50);
        var samplerObj2 = GetInnerSampler(cache2!);

        samplerObj2.ShouldNotBeSameAs(samplerObj1);
    }

    private static DiagnosticsRuntimeSampler.CachedSampler? GetCachedSampler(DiagnosticsRuntimeSampler sampler) =>
        sampler.TestingCachedSampler;

    private static double GetProbability(DiagnosticsRuntimeSampler.CachedSampler cache) => cache.Probability;

    private static Sampler GetInnerSampler(DiagnosticsRuntimeSampler.CachedSampler cache) => cache.Sampler;
}
