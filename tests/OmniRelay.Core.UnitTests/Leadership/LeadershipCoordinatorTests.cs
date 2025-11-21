using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;
using Xunit;

namespace OmniRelay.Core.UnitTests.Leadership;

public sealed class LeadershipCoordinatorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Coordinator_ElectsSingleLeaderAndFailsOver()
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

        var snapshotA = await WaitForTokenAsync(coordinatorA, TestContext.Current.CancellationToken);
        var initialToken = snapshotA.Tokens[0];
        (initialToken.LeaderId is "node-a" or "node-b").ShouldBeTrue();

        var leaderCoordinator = initialToken.LeaderId == "node-a" ? coordinatorA : coordinatorB;
        var followerCoordinator = initialToken.LeaderId == "node-a" ? coordinatorB : coordinatorA;

        await leaderCoordinator.StopAsync(TestContext.Current.CancellationToken);

        var followerSnapshot = await WaitForTokenAsync(
            followerCoordinator,
            TestContext.Current.CancellationToken,
            token => token.LeaderId != initialToken.LeaderId);
        var failoverToken = followerSnapshot.Tokens[0];

        failoverToken.LeaderId.ShouldNotBe(initialToken.LeaderId);
        (failoverToken.FenceToken > initialToken.FenceToken).ShouldBeTrue();

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

    private static async Task<LeadershipSnapshot> WaitForTokenAsync(
        LeadershipCoordinator coordinator,
        CancellationToken cancellationToken,
        Func<LeadershipToken, bool>? predicate = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (true)
        {
            var snapshot = coordinator.Snapshot();
            if (snapshot.Tokens.Length > 0)
            {
                var token = snapshot.Tokens[0];
                if (predicate is null || predicate(token))
                {
                    return snapshot;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Snapshot_ReturnsEmptyInitially()
    {
        var store = new InMemoryLeadershipStore();
        var hub = new LeadershipEventHub(NullLogger<LeadershipEventHub>.Instance);

        var options = new LeadershipOptions
        {
            Enabled = true,
            NodeId = "node-a",
            EvaluationInterval = TimeSpan.FromMilliseconds(100),
            LeaseDuration = TimeSpan.FromSeconds(5),
            RenewalLeadTime = TimeSpan.FromSeconds(2)
        };

        var coordinator = new LeadershipCoordinator(options, store, NullMeshGossipAgent.Instance, hub, NullLogger<LeadershipCoordinator>.Instance);
        var snapshot = coordinator.Snapshot();

        snapshot.ShouldNotBeNull();
        snapshot.Tokens.ShouldBeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Coordinator_CanBeStartedAndStopped()
    {
        var store = new InMemoryLeadershipStore();
        var hub = new LeadershipEventHub(NullLogger<LeadershipEventHub>.Instance);

        var options = new LeadershipOptions
        {
            Enabled = true,
            NodeId = "node-a",
            EvaluationInterval = TimeSpan.FromMilliseconds(50),
            LeaseDuration = TimeSpan.FromSeconds(2),
            RenewalLeadTime = TimeSpan.FromMilliseconds(500)
        };
        options.Scopes.Add(new LeadershipScopeDescriptor
        {
            ScopeId = LeadershipScope.GlobalControl.ScopeId,
            Kind = LeadershipScopeKinds.Global
        });

        var coordinator = new LeadershipCoordinator(options, store, NullMeshGossipAgent.Instance, hub, NullLogger<LeadershipCoordinator>.Instance);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        await coordinator.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MultipleCoordinators_CompeteForLeadership()
    {
        var store = new InMemoryLeadershipStore();
        var hubA = new LeadershipEventHub(NullLogger<LeadershipEventHub>.Instance);
        var hubB = new LeadershipEventHub(NullLogger<LeadershipEventHub>.Instance);
        var hubC = new LeadershipEventHub(NullLogger<LeadershipEventHub>.Instance);

        var scopeDescriptor = new LeadershipScopeDescriptor
        {
            ScopeId = "test-scope",
            Kind = LeadershipScopeKinds.Custom
        };

        var optionsA = CreateOptions("node-a", scopeDescriptor);
        var optionsB = CreateOptions("node-b", scopeDescriptor);
        var optionsC = CreateOptions("node-c", scopeDescriptor);

        var coordinatorA = new LeadershipCoordinator(optionsA, store, NullMeshGossipAgent.Instance, hubA, NullLogger<LeadershipCoordinator>.Instance);
        var coordinatorB = new LeadershipCoordinator(optionsB, store, NullMeshGossipAgent.Instance, hubB, NullLogger<LeadershipCoordinator>.Instance);
        var coordinatorC = new LeadershipCoordinator(optionsC, store, NullMeshGossipAgent.Instance, hubC, NullLogger<LeadershipCoordinator>.Instance);

        await coordinatorA.StartAsync(TestContext.Current.CancellationToken);
        await coordinatorB.StartAsync(TestContext.Current.CancellationToken);
        await coordinatorC.StartAsync(TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        var snapshotA = coordinatorA.Snapshot();
        var snapshotB = coordinatorB.Snapshot();
        var snapshotC = coordinatorC.Snapshot();

        var leadersCount = 0;
        if (snapshotA.Tokens.Any(t => t.LeaderId == "node-a"))
        {
            leadersCount++;
        }

        if (snapshotB.Tokens.Any(t => t.LeaderId == "node-b"))
        {
            leadersCount++;
        }

        if (snapshotC.Tokens.Any(t => t.LeaderId == "node-c"))
        {
            leadersCount++;
        }

        leadersCount.ShouldBe(1);

        await coordinatorA.StopAsync(TestContext.Current.CancellationToken);
        await coordinatorB.StopAsync(TestContext.Current.CancellationToken);
        await coordinatorC.StopAsync(TestContext.Current.CancellationToken);
    }
}
