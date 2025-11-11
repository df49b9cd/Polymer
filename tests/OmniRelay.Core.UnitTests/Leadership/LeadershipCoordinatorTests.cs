using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;
using Xunit;

namespace OmniRelay.Core.UnitTests.Leadership;

public sealed class LeadershipCoordinatorTests
{
    [Fact]
    public async Task Coordinator_ElectsSingleLeaderAndFailsOver()
    {
        var store = new InMemoryLeadershipStore();
        var hubA = new LeadershipEventHub(NullLogger<LeadershipEventHub>.Instance);
        var hubB = new LeadershipEventHub(NullLogger<LeadershipEventHub>.Instance);

        var scopeDescriptor = new LeadershipScopeDescriptor
        {
            ScopeId = LeadershipScope.GlobalControl.ScopeId,
            Kind = LeadershipScopeKinds.Global
        };

        var optionsA = CreateOptions("node-a", scopeDescriptor);
        var optionsB = CreateOptions("node-b", scopeDescriptor);

        var coordinatorA = new LeadershipCoordinator(optionsA, store, NullMeshGossipAgent.Instance, hubA, NullLogger<LeadershipCoordinator>.Instance);
        var coordinatorB = new LeadershipCoordinator(optionsB, store, NullMeshGossipAgent.Instance, hubB, NullLogger<LeadershipCoordinator>.Instance);

        await coordinatorA.StartAsync(TestContext.Current.CancellationToken);
        await coordinatorB.StartAsync(TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        var snapshotA = coordinatorA.Snapshot();
        Assert.NotEmpty(snapshotA.Tokens);
        var initialToken = snapshotA.Tokens[0];
        Assert.True(initialToken.LeaderId is "node-a" or "node-b");

        var leaderCoordinator = initialToken.LeaderId == "node-a" ? coordinatorA : coordinatorB;
        var followerCoordinator = initialToken.LeaderId == "node-a" ? coordinatorB : coordinatorA;

        await leaderCoordinator.StopAsync(TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        var followerSnapshot = followerCoordinator.Snapshot();
        Assert.NotEmpty(followerSnapshot.Tokens);
        var failoverToken = followerSnapshot.Tokens[0];

        Assert.NotEqual(initialToken.LeaderId, failoverToken.LeaderId);
        Assert.True(failoverToken.FenceToken > initialToken.FenceToken);

        await followerCoordinator.StopAsync(TestContext.Current.CancellationToken);
    }

    private static LeadershipOptions CreateOptions(string nodeId, LeadershipScopeDescriptor descriptor)
    {
        var options = new LeadershipOptions
        {
            Enabled = true,
            NodeId = nodeId,
            EvaluationInterval = TimeSpan.FromMilliseconds(25),
            LeaseDuration = TimeSpan.FromMilliseconds(200),
            RenewalLeadTime = TimeSpan.FromMilliseconds(60)
        };
        options.Scopes.Add(new LeadershipScopeDescriptor
        {
            ScopeId = descriptor.ScopeId,
            Kind = descriptor.Kind
        });
        return options;
    }
}
