using System.Collections.Immutable;
using NSubstitute;
using OmniRelay.Core.Transport;
using OmniRelay.Transport.Grpc;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherHealthEvaluatorTests
{
    [Fact]
    public void Evaluate_WhenDispatcherNotRunning_ReportsStatusIssue()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        var result = DispatcherHealthEvaluator.Evaluate(dispatcher);

        Assert.False(result.IsReady);
        Assert.Contains("dispatcher-status:Created", result.Issues);
    }

    [Fact]
    public async Task Evaluate_WithGrpcOutboundReportsIssues()
    {
        var initial = await EvaluateAsync(CreateSnapshot(isStarted: false, peers: []));
        Assert.Contains("grpc:remote:unary:default:not-started", initial.Issues);

        var noPeers = await EvaluateAsync(CreateSnapshot(isStarted: true, peers: []));
        Assert.Contains("grpc:remote:unary:default:no-peers", noPeers.Issues);

        var noAvailable = await EvaluateAsync(CreateSnapshot(
            isStarted: true,
            peers: [new Uri("http://localhost")],
            summaries: [new GrpcPeerSummary(new Uri("http://localhost"), Core.Peers.PeerState.Unavailable, 0, null, null, 0, 0, null, null, null, null)
            ]));
        Assert.Contains("grpc:remote:unary:default:no-available-peers", noAvailable.Issues);

        var healthy = await EvaluateAsync(CreateSnapshot(
            isStarted: true,
            peers: [new Uri("http://localhost")],
            summaries: [new GrpcPeerSummary(new Uri("http://localhost"), Core.Peers.PeerState.Available, 0, null, null, 0, 0, null, null, null, null)
            ]));
        Assert.True(healthy.IsReady);
        Assert.Empty(healthy.Issues);
    }

    private static async Task<DispatcherReadinessResult> EvaluateAsync(GrpcOutboundSnapshot snapshot)
    {
        var options = new DispatcherOptions("svc");
        var outbound = Substitute.For<IUnaryOutbound, IOutboundDiagnostic>();
        ((IOutboundDiagnostic)outbound).GetOutboundDiagnostics().Returns(snapshot);
        options.AddUnaryOutbound("remote", null, outbound);
        var dispatcher = new Dispatcher(options);
        await dispatcher.StartOrThrowAsync();
        try
        {
            return DispatcherHealthEvaluator.Evaluate(dispatcher);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync();
        }
    }

    private static GrpcOutboundSnapshot CreateSnapshot(
        bool isStarted,
        IReadOnlyList<Uri> peers,
        ImmutableArray<GrpcPeerSummary>? summaries = null)
    {
        summaries ??= [];
        return new GrpcOutboundSnapshot(
            "remote",
            peers,
            "round-robin",
            isStarted,
            summaries.Value,
            []);
    }
}
