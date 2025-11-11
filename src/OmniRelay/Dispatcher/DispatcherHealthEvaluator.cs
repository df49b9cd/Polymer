using System.Collections.Immutable;
using OmniRelay.Core.Peers;
using OmniRelay.Transport.Grpc;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Simple readiness evaluator that checks dispatcher status and gRPC outbound bindings.
/// </summary>
public static class DispatcherHealthEvaluator
{
    /// <summary>
    /// Evaluates the dispatcher and returns readiness status with issue codes when not ready.
    /// </summary>
    public static DispatcherReadinessResult Evaluate(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        var snapshot = dispatcher.Introspect();
        var issues = new List<string>();

        if (snapshot.Status != DispatcherStatus.Running)
        {
            issues.Add($"dispatcher-status:{snapshot.Status}");
        }

        foreach (var outbound in snapshot.Outbounds)
        {
            EvaluateBindings(outbound.Service, "unary", outbound.Unary, issues);
            EvaluateBindings(outbound.Service, "oneway", outbound.Oneway, issues);
            EvaluateBindings(outbound.Service, "stream", outbound.Stream, issues);
            EvaluateBindings(outbound.Service, "client-stream", outbound.ClientStream, issues);
            EvaluateBindings(outbound.Service, "duplex", outbound.Duplex, issues);
        }

        return new DispatcherReadinessResult(issues.Count == 0, [.. issues]);
    }

    private static void EvaluateBindings(
        string service,
        string kind,
        ImmutableArray<OutboundBindingDescriptor> descriptors,
        ICollection<string> issues)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Metadata is GrpcOutboundSnapshot grpcSnapshot)
            {
                EvaluateGrpcBinding(service, kind, descriptor.Key, grpcSnapshot, issues);
            }
        }
    }

    private static void EvaluateGrpcBinding(
        string service,
        string kind,
        string key,
        GrpcOutboundSnapshot snapshot,
        ICollection<string> issues)
    {
        var identifier = $"grpc:{service}:{kind}:{key}";

        if (!snapshot.IsStarted)
        {
            issues.Add($"{identifier}:not-started");
            return;
        }

        if (snapshot.Peers.Count == 0)
        {
            issues.Add($"{identifier}:no-peers");
            return;
        }

        if (snapshot.PeerSummaries is { Count: > 0 } summaries)
        {
            if (!summaries.Any(peer => peer.State == PeerState.Available))
            {
                issues.Add($"{identifier}:no-available-peers");
            }
        }
        else
        {
            issues.Add($"{identifier}:no-peer-metrics");
        }
    }
}

/// <summary>Result of dispatcher readiness evaluation.</summary>
public sealed record DispatcherReadinessResult(bool IsReady, ImmutableArray<string> Issues)
{
    public bool IsReady { get; init; } = IsReady;

    public ImmutableArray<string> Issues { get; init; } = Issues;
}
