using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Diagnostics;
using OpenTelemetry.Trace;
using Xunit;

namespace OmniRelay.Tests.Core.Diagnostics;

public class DiagnosticsRuntimeSamplerTests
{
    [Fact]
    public void NullRuntime_UsesFallbackSampler()
    {
        var sampler = new DiagnosticsRuntimeSampler(null, new AlwaysOffSampler());
        var result = sampler.ShouldSample(CreateParameters(CreateTraceId(0x00)));
        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    [Fact]
    public void NullProbability_UsesFallbackSampler()
    {
        var runtime = new FakeDiagnosticsRuntime();
        var sampler = new DiagnosticsRuntimeSampler(runtime, new AlwaysOffSampler());
        var result = sampler.ShouldSample(CreateParameters(CreateTraceId(0x00)));
        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    [Fact]
    public void ZeroProbability_DropsNewTraces()
    {
        var runtime = new FakeDiagnosticsRuntime { Probability = 0d };
        var sampler = new DiagnosticsRuntimeSampler(runtime);
        var result = sampler.ShouldSample(CreateParameters(CreateTraceId(0x00)));
        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    [Fact]
    public void ZeroProbability_RespectsRecordedParent()
    {
        var runtime = new FakeDiagnosticsRuntime { Probability = 0d };
        var sampler = new DiagnosticsRuntimeSampler(runtime);

        var parentContext = new ActivityContext(
            CreateTraceId(0x11),
            ActivitySpanId.CreateFromBytes([0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11]),
            ActivityTraceFlags.Recorded);

        var result = sampler.ShouldSample(CreateParameters(CreateTraceId(0x00), parentContext));
        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
    }

    [Fact]
    public void RatioProbability_AppliesDynamicSampler()
    {
        const double probability = 0.5d;
        var runtime = new FakeDiagnosticsRuntime { Probability = probability };
        var sampler = new DiagnosticsRuntimeSampler(runtime);

        var sampleResult = sampler.ShouldSample(CreateParameters(CreateTraceId(0x00)));
        var dropTraceId = FindDropTraceId(probability);
        var dropResult = sampler.ShouldSample(CreateParameters(dropTraceId));

        Assert.Equal(SamplingDecision.RecordAndSample, sampleResult.Decision);
        Assert.Equal(SamplingDecision.Drop, dropResult.Decision);
    }

    [Fact]
    public void FullProbability_DelegatesToFallback()
    {
        var runtime = new FakeDiagnosticsRuntime { Probability = 1d };
        var sampler = new DiagnosticsRuntimeSampler(runtime, new AlwaysOffSampler());

        var result = sampler.ShouldSample(CreateParameters(CreateTraceId(0x00)));
        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    private static SamplingParameters CreateParameters(ActivityTraceId traceId, ActivityContext parentContext = default) => new(
            parentContext,
            traceId,
            "test-activity",
            ActivityKind.Server,
            null,
            null);

    private static ActivityTraceId CreateTraceId(byte value)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(value);
        return ActivityTraceId.CreateFromBytes(bytes);
    }

    private static ActivityTraceId FindDropTraceId(double probability)
    {
        var referenceSampler = new TraceIdRatioBasedSampler(probability);

        for (var value = 0; value <= byte.MaxValue; value++)
        {
            var traceId = CreateTraceId((byte)value);
            var result = referenceSampler.ShouldSample(
                new SamplingParameters(default, traceId, "reference", ActivityKind.Server, null, null));
            if (result.Decision == SamplingDecision.Drop)
            {
                return traceId;
            }
        }

        return CreateTraceId(0xFF);
    }

    private sealed class FakeDiagnosticsRuntime : IDiagnosticsRuntime
    {
        public LogLevel? MinimumLogLevel => null;

        public double? Probability { get; set; }

        public double? TraceSamplingProbability => Probability;

        public void SetMinimumLogLevel(LogLevel? level)
        {
        }

        public void SetTraceSamplingProbability(double? probability) => Probability = probability;
    }
}
