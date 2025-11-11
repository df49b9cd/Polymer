using System.Diagnostics.Metrics;

namespace OmniRelay.Core.Leadership;

/// <summary>Prometheus/OpenTelemetry meters describing leadership churn and safety signals.</summary>
internal static class LeadershipMetrics
{
    private static readonly Meter Meter = new("OmniRelay.Core.Leadership");

    private static readonly Counter<long> TransitionCounter = Meter.CreateCounter<long>(
        "mesh_leadership_transitions_total",
        unit: "events",
        description: "Leadership transitions per scope and outcome.");

    private static readonly Histogram<double> ElectionDurationHistogram = Meter.CreateHistogram<double>(
        "mesh_leadership_election_duration_ms",
        unit: "ms",
        description: "Observed election durations per scope.");

    private static readonly Counter<long> SplitBrainCounter = Meter.CreateCounter<long>(
        "mesh_leadership_split_brain_total",
        unit: "events",
        description: "Detected split-brain conditions (leader missing from membership or expired).");

    public static void RecordTransition(string scope, string scopeKind, LeadershipEventKind kind, string leaderId)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("mesh.scope", scope),
            new KeyValuePair<string, object?>("mesh.scope_kind", scopeKind),
            new KeyValuePair<string, object?>("mesh.leader", leaderId),
            new KeyValuePair<string, object?>("mesh.transition", kind.ToString().ToLowerInvariant())
        };

        TransitionCounter.Add(1, tags);
    }

    public static void RecordElectionDuration(string scope, string scopeKind, double durationMilliseconds)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("mesh.scope", scope),
            new KeyValuePair<string, object?>("mesh.scope_kind", scopeKind)
        };

        ElectionDurationHistogram.Record(durationMilliseconds, tags);
    }

    public static void RecordSplitBrain(string scope, string? incumbent)
    {
        var tags = string.IsNullOrWhiteSpace(incumbent)
            ? new[] { new KeyValuePair<string, object?>("mesh.scope", scope) }
            : new[]
            {
                new KeyValuePair<string, object?>("mesh.scope", scope),
                new KeyValuePair<string, object?>("mesh.leader", incumbent)
            };

        SplitBrainCounter.Add(1, tags);
    }
}
