using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;
using OmniRelay.IntegrationTests.Support;
using Xunit;

namespace OmniRelay.IntegrationTests;

public sealed class LeadershipIntegrationTests(ITestOutputHelper output) : IntegrationTest(output)
{
    private static readonly string ScopeId = LeadershipScope.GlobalControl.ScopeId;
    private static readonly TimeSpan ElectionTimeout = TimeSpan.FromSeconds(15);
    // CI containers can be slower to surface the renewed lease, so give them more time.
    private readonly TimeSpan _renewalTimeout = TimeSpan.FromSeconds(20);

    [Fact(Timeout = 60_000)]
    public async ValueTask LeadershipCoordinator_SharedStore_ElectsSingleLeaderAndRenews()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryLeadershipStore();
        var coordinatorA = CreateCoordinator("node-alpha", store);
        var coordinatorB = CreateCoordinator("node-bravo", store);

        await StartCoordinatorsAsync(ct, coordinatorA, coordinatorB);

        try
        {
            await WaitForConditionAsync(
                "single leader election",
                () => HasConsistentLeader(coordinatorA, coordinatorB),
                () => DescribeSnapshot(coordinatorA, coordinatorB),
                ElectionTimeout,
                ct);

            var tokenA = coordinatorA.GetToken(ScopeId)!;
            var tokenB = coordinatorB.GetToken(ScopeId)!;
            tokenA.LeaderId.ShouldBe(tokenB.LeaderId);
            tokenA.LeaderId.ShouldBeOneOf(coordinatorA.NodeId, coordinatorB.NodeId);

            var initialExpiry = tokenA.ExpiresAt;
            await WaitForConditionAsync(
                "lease renewal",
                () => GetActiveToken(coordinatorA, coordinatorB)?.ExpiresAt > initialExpiry,
                () => DescribeSnapshot(coordinatorA, coordinatorB),
                _renewalTimeout,
                ct);
        }
        finally
        {
            await StopAndDisposeAsync(coordinatorA, coordinatorB);
        }
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask LeadershipCoordinator_FailoverAfterLeaderStops_ElectsNewLeader()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryLeadershipStore();
        var coordinatorA = CreateCoordinator("node-alpha", store);
        var coordinatorB = CreateCoordinator("node-bravo", store);

        await StartCoordinatorsAsync(ct, coordinatorA, coordinatorB);

        LeadershipCoordinator incumbent;
        LeadershipCoordinator standby;

        try
        {
            await WaitForConditionAsync(
                "initial leader election",
                () => HasConsistentLeader(coordinatorA, coordinatorB),
                () => DescribeSnapshot(coordinatorA, coordinatorB),
                ElectionTimeout,
                ct);

            var leaderId = coordinatorA.GetToken(ScopeId)!.LeaderId;
            incumbent = leaderId == coordinatorA.NodeId ? coordinatorA : coordinatorB;
            standby = incumbent == coordinatorA ? coordinatorB : coordinatorA;

            await incumbent.StopAsync(ct);
            incumbent.Dispose();

            await WaitForConditionAsync(
                "standby takeover",
                () =>
                {
                    var token = standby.GetToken(ScopeId);
                    return token is not null && token.LeaderId == standby.NodeId;
                },
                () => DescribeSnapshot(standby),
                ElectionTimeout,
                ct);
        }
        finally
        {
            await StopAndDisposeAsync(coordinatorA, coordinatorB);
        }
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask LeadershipCoordinator_DefersElectionUntilGossipReportsHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryLeadershipStore();
        var gossip = new TestMeshGossipAgent("node-gossip");
        var coordinator = CreateCoordinator("node-gossip", store, gossip);

        await coordinator.StartAsync(ct);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), ct);
            coordinator.GetToken(ScopeId).ShouldBeNull();

            gossip.UpdateMembers(new MeshGossipMemberSnapshot
            {
                NodeId = gossip.LocalMetadata.NodeId,
                Status = MeshGossipMemberStatus.Alive,
                LastSeen = DateTimeOffset.UtcNow,
                Metadata = gossip.LocalMetadata
            });

            await WaitForConditionAsync(
                "gossip-ready election",
                () => coordinator.GetToken(ScopeId)?.LeaderId == coordinator.NodeId,
                () => DescribeSnapshot(coordinator),
                ElectionTimeout,
                ct);
        }
        finally
        {
            await StopAndDisposeAsync(coordinator);
        }
    }

    private LeadershipCoordinator CreateCoordinator(
        string nodeId,
        InMemoryLeadershipStore store,
        IMeshGossipAgent? gossipAgent = null)
    {
        var options = CreateLeadershipOptions(nodeId);
        var coordinatorLogger = LoggerFactory.CreateLogger<LeadershipCoordinator>();
        var hubLogger = LoggerFactory.CreateLogger<LeadershipEventHub>();
        var eventHub = new LeadershipEventHub(hubLogger);
        var agent = gossipAgent ?? NullMeshGossipAgent.Instance;
        return new LeadershipCoordinator(options, store, agent, eventHub, coordinatorLogger);
    }

    private static LeadershipOptions CreateLeadershipOptions(string nodeId)
    {
        var options = new LeadershipOptions
        {
            NodeId = nodeId,
            LeaseDuration = TimeSpan.FromSeconds(3),
            RenewalLeadTime = TimeSpan.FromSeconds(1),
            EvaluationInterval = TimeSpan.FromMilliseconds(150),
            ElectionBackoff = TimeSpan.FromMilliseconds(150),
            MaxElectionWindow = TimeSpan.FromSeconds(4)
        };

        options.Scopes.Add(new LeadershipScopeDescriptor
        {
            ScopeId = ScopeId,
            Kind = LeadershipScopeKinds.Global
        });

        return options;
    }

    private static bool HasConsistentLeader(params LeadershipCoordinator[] coordinators)
    {
        var tokens = coordinators
            .Select(coordinator => coordinator.GetToken(ScopeId))
            .Where(token => token is not null)
            .ToArray();

        if (tokens.Length != coordinators.Length)
        {
            return false;
        }

        return tokens.All(token => token!.LeaderId == tokens[0]!.LeaderId);
    }

    private static LeadershipToken? GetActiveToken(params LeadershipCoordinator[] coordinators)
    {
        foreach (var coordinator in coordinators)
        {
            var token = coordinator.GetToken(ScopeId);
            if (token is not null)
            {
                return token;
            }
        }

        return null;
    }

    private static async Task StartCoordinatorsAsync(CancellationToken cancellationToken, params LeadershipCoordinator[] coordinators)
    {
        foreach (var coordinator in coordinators)
        {
            await coordinator.StartAsync(cancellationToken);
        }
    }

    private static async Task StopAndDisposeAsync(params LeadershipCoordinator[] coordinators)
    {
        foreach (var coordinator in coordinators)
        {
            if (coordinator is null)
            {
                continue;
            }

            try
            {
                await coordinator.StopAsync(CancellationToken.None);
            }
            finally
            {
                coordinator.Dispose();
            }
        }
    }

    private static async Task WaitForConditionAsync(
        string description,
        Func<bool> predicate,
        Func<string> diagnostics,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();

                if (predicate())
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description}.{Environment.NewLine}{diagnostics()}");
        }
    }

    private static string DescribeSnapshot(params LeadershipCoordinator[] coordinators)
    {
        var builder = new StringBuilder();
        foreach (var coordinator in coordinators)
        {
            var token = coordinator.GetToken(ScopeId);
            builder.Append(coordinator.NodeId);
            builder.Append(": ");
            builder.Append(token is null ? "no-leader" : $"{token.LeaderId}/term-{token.Term}/fence-{token.FenceToken}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private sealed class TestMeshGossipAgent : IMeshGossipAgent
    {
        private readonly object _lock = new();
        private MeshGossipClusterView _snapshot;

        public TestMeshGossipAgent(string nodeId)
        {
            LocalMetadata = new MeshGossipMemberMetadata
            {
                NodeId = nodeId,
                Role = "dispatcher",
                ClusterId = "integration",
                Region = "local",
                MeshVersion = "integration-tests",
                Http3Support = true
            };

            _snapshot = new MeshGossipClusterView(
                DateTimeOffset.UtcNow,
                ImmutableArray<MeshGossipMemberSnapshot>.Empty,
                LocalMetadata.NodeId,
                MeshGossipOptions.CurrentSchemaVersion);
        }

        public bool IsEnabled => true;

        public MeshGossipMemberMetadata LocalMetadata { get; }

        public MeshGossipClusterView Snapshot()
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }

        public void UpdateMembers(params MeshGossipMemberSnapshot[] members)
        {
            var immutable = members.Length == 0
                ? ImmutableArray<MeshGossipMemberSnapshot>.Empty
                : members.ToImmutableArray();

            lock (_lock)
            {
                _snapshot = new MeshGossipClusterView(
                    DateTimeOffset.UtcNow,
                    immutable,
                    LocalMetadata.NodeId,
                    MeshGossipOptions.CurrentSchemaVersion);
            }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
