using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;

namespace OmniRelay.HyperscaleFeatureTests.Infrastructure;

internal sealed class HyperscaleLeadershipCluster : IAsyncDisposable
{
    private readonly LaggyLeadershipStore _store;
    private readonly HyperscaleLeadershipClusterOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<LeadershipScope> _scopes;
    private readonly List<LeadershipNode> _nodes = new();
    private readonly HyperscaleGossipRegistry _gossipRegistry = new();
    private bool _started;

    public HyperscaleLeadershipCluster(
        LaggyLeadershipStore store,
        HyperscaleLeadershipClusterOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _scopes = BuildScopes(options);
        BuildNodes();
    }

    public IReadOnlyList<LeadershipScope> Scopes => _scopes;

    public IReadOnlyList<ILeadershipObserver> Observers => _nodes
        .Select(node => (ILeadershipObserver)node.Coordinator)
        .ToArray();

    public LeadershipSnapshot Snapshot()
    {
        var aggregate = new Dictionary<string, LeadershipToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _nodes.Where(node => node.IsRunning))
        {
            var snapshot = node.Coordinator.Snapshot();
            foreach (var token in snapshot.Tokens)
            {
                if (!aggregate.TryGetValue(token.Scope, out var existing) || token.FenceToken > existing.FenceToken)
                {
                    aggregate[token.Scope] = token;
                }
            }
        }

        if (aggregate.Count == 0)
        {
            return _nodes.First().Coordinator.Snapshot();
        }

        return new LeadershipSnapshot(DateTimeOffset.UtcNow, aggregate.Values.ToImmutableArray());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        foreach (var node in _nodes)
        {
            await node.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        _started = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return;
        }

        foreach (var node in _nodes)
        {
            await node.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _started = false;
    }

    public async Task WaitForStableLeadershipAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var success = await WaitForConditionAsync(() =>
        {
            var snapshot = Snapshot();
            return _scopes.All(scope => snapshot.Find(scope.ScopeId) is { } token && !token.IsExpired(DateTimeOffset.UtcNow));
        }, timeout, cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            throw new TimeoutException("Leadership snapshot did not converge for all scopes.");
        }
    }

    public LeadershipToken? GetToken(string scopeId) =>
        Snapshot().Find(scopeId);

    public async Task<TimeSpan> ForceFailoverAsync(string scopeId, CancellationToken cancellationToken)
    {
        var incumbent = await RequireActiveTokenAsync(scopeId, cancellationToken).ConfigureAwait(false);

        var node = _nodes.FirstOrDefault(n => string.Equals(n.NodeId, incumbent.LeaderId, StringComparison.Ordinal));
        if (node is null)
        {
            throw new InvalidOperationException($"Leader '{incumbent.LeaderId}' not tracked in cluster.");
        }

        await node.StopAsync(cancellationToken).ConfigureAwait(false);
        await _store.TryReleaseAsync(scopeId, ToLeaseRecord(incumbent), cancellationToken).ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var failoverCompleted = await WaitForConditionAsync(() =>
        {
            var token = GetToken(scopeId);
            return token is not null && !string.Equals(token.LeaderId, incumbent.LeaderId, StringComparison.Ordinal);
        }, _options.MaxElectionWindow + TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

        if (!failoverCompleted)
        {
            throw new TimeoutException($"Leader for scope '{scopeId}' did not fail over within SLA.");
        }

        await node.StartAsync(cancellationToken).ConfigureAwait(false);

        return DateTimeOffset.UtcNow - startedAt;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var node in _nodes)
        {
            node.Dispose();
        }
    }

    private void BuildNodes()
    {
        foreach (var region in _options.Regions)
        {
            for (var index = 0; index < _options.NodesPerRegion; index++)
            {
                var nodeId = $"{region}-dispatcher-{index + 1:D2}";
                var metadata = new MeshGossipMemberMetadata
                {
                    NodeId = nodeId,
                    Role = "dispatcher",
                    ClusterId = _options.ClusterId,
                    Region = region,
                    MeshVersion = _options.MeshVersion,
                    Http3Support = index % 2 == 0,
                    Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["rack"] = $"rack-{index % 8:D2}",
                        ["zone"] = $"{region}-zone-{index % 3:D2}"
                    }
                };

                var agent = _gossipRegistry.CreateAgent(metadata);
                var options = CreateLeadershipOptions(nodeId);
                var eventHub = new LeadershipEventHub(_loggerFactory.CreateLogger<LeadershipEventHub>());
                var coordinatorLogger = _loggerFactory.CreateLogger<LeadershipCoordinator>();
                var coordinator = new LeadershipCoordinator(options, _store, agent, eventHub, coordinatorLogger);

                _nodes.Add(new LeadershipNode(nodeId, coordinator, agent));
            }
        }
    }

    private LeadershipOptions CreateLeadershipOptions(string nodeId)
    {
        var options = new LeadershipOptions
        {
            NodeId = nodeId,
            LeaseDuration = _options.LeaseDuration,
            RenewalLeadTime = _options.RenewalLeadTime,
            EvaluationInterval = _options.EvaluationInterval,
            ElectionBackoff = _options.ElectionBackoff,
            MaxElectionWindow = _options.MaxElectionWindow
        };

        options.Scopes.Add(new LeadershipScopeDescriptor
        {
            ScopeId = LeadershipScope.GlobalControl.ScopeId,
            Kind = LeadershipScopeKinds.Global
        });

        foreach (var scope in _scopes.Where(scope => !string.Equals(scope.ScopeId, LeadershipScope.GlobalControl.ScopeId, StringComparison.OrdinalIgnoreCase)))
        {
            options.Scopes.Add(new LeadershipScopeDescriptor
            {
                Namespace = scope.Labels.TryGetValue("namespace", out var @namespace) ? @namespace : scope.ScopeId,
                ShardId = scope.Labels.TryGetValue("shardId", out var shardId) ? shardId : scope.ScopeId,
                Kind = scope.ScopeKind
            });
        }

        return options;
    }

    private static List<LeadershipScope> BuildScopes(HyperscaleLeadershipClusterOptions options)
    {
        var scopes = new List<LeadershipScope> { LeadershipScope.GlobalControl };
        foreach (var region in options.Regions)
        {
            foreach (var ns in options.Namespaces)
            {
                for (var shard = 0; shard < options.ShardsPerNamespace; shard++)
                {
                    var namespaceId = $"{ns}.{region}";
                    var shardId = $"{region}-shard-{shard:D2}";
                    scopes.Add(LeadershipScope.ForShard(namespaceId, shardId));
                }
            }
        }

        return scopes;
    }

    private static LeadershipLeaseRecord ToLeaseRecord(LeadershipToken token) =>
        new(token.Scope, token.LeaderId, token.Term, token.FenceToken, token.IssuedAt, token.ExpiresAt);

    private async Task<LeadershipToken> RequireActiveTokenAsync(string scopeId, CancellationToken cancellationToken)
    {
        LeadershipToken? token = null;
        var success = await WaitForConditionAsync(() =>
        {
            token = GetToken(scopeId);
            return token is not null && !token.IsExpired(DateTimeOffset.UtcNow);
        }, _options.MaxElectionWindow + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        if (!success || token is null)
        {
            throw new InvalidOperationException($"No incumbent leader found for scope '{scopeId}'.");
        }

        return token;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        return predicate();
    }

    private sealed class LeadershipNode : IDisposable
    {
        public LeadershipNode(string nodeId, LeadershipCoordinator coordinator, HyperscaleMeshGossipAgent agent)
        {
            NodeId = nodeId;
            Coordinator = coordinator;
            Agent = agent;
        }

        public string NodeId { get; }

        public LeadershipCoordinator Coordinator { get; }

        public HyperscaleMeshGossipAgent Agent { get; }

        public bool IsRunning { get; private set; }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            if (IsRunning)
            {
                return;
            }

            Agent.MarkAlive();
            await Coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
            IsRunning = true;
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            if (!IsRunning)
            {
                return;
            }

            await Coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
            Agent.MarkLeft();
            IsRunning = false;
        }

        public void Dispose()
        {
            Coordinator.Dispose();
        }
    }

    private sealed class HyperscaleGossipRegistry
    {
        private readonly ConcurrentDictionary<string, MeshGossipMemberSnapshot> _members = new(StringComparer.OrdinalIgnoreCase);

        public HyperscaleMeshGossipAgent CreateAgent(MeshGossipMemberMetadata metadata)
        {
            var snapshot = new MeshGossipMemberSnapshot
            {
                NodeId = metadata.NodeId,
                Status = MeshGossipMemberStatus.Alive,
                LastSeen = DateTimeOffset.UtcNow,
                Metadata = metadata
            };

            _members[metadata.NodeId] = snapshot;
            return new HyperscaleMeshGossipAgent(this, metadata);
        }

        public MeshGossipClusterView Snapshot(string localNodeId)
        {
            var members = _members.Values.OrderBy(member => member.NodeId, StringComparer.Ordinal).ToImmutableArray();
            return new MeshGossipClusterView(
                DateTimeOffset.UtcNow,
                members,
                localNodeId,
                MeshGossipOptions.CurrentSchemaVersion);
        }

        public void UpdateStatus(string nodeId, MeshGossipMemberStatus status)
        {
            _members.AddOrUpdate(
                nodeId,
                _ => new MeshGossipMemberSnapshot
                {
                    NodeId = nodeId,
                    Status = status,
                    LastSeen = DateTimeOffset.UtcNow,
                    Metadata = new MeshGossipMemberMetadata { NodeId = nodeId }
                },
                (_, snapshot) => new MeshGossipMemberSnapshot
                {
                    NodeId = snapshot.NodeId,
                    Status = status,
                    LastSeen = DateTimeOffset.UtcNow,
                    Metadata = snapshot.Metadata
                });
        }
    }

    private sealed class HyperscaleMeshGossipAgent : IMeshGossipAgent
    {
        private readonly HyperscaleGossipRegistry _registry;

        public HyperscaleMeshGossipAgent(HyperscaleGossipRegistry registry, MeshGossipMemberMetadata metadata)
        {
            _registry = registry;
            LocalMetadata = metadata;
        }

        public string NodeId => LocalMetadata.NodeId;

        public MeshGossipMemberMetadata LocalMetadata { get; }

        public bool IsEnabled => true;

        public MeshGossipClusterView Snapshot() => _registry.Snapshot(LocalMetadata.NodeId);

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            MarkLeft();
            return ValueTask.CompletedTask;
        }

        public void MarkAlive() => _registry.UpdateStatus(LocalMetadata.NodeId, MeshGossipMemberStatus.Alive);

        public void MarkLeft() => _registry.UpdateStatus(LocalMetadata.NodeId, MeshGossipMemberStatus.Left);
    }
}

internal sealed class HyperscaleLeadershipClusterOptions
{
    public IReadOnlyList<string> Regions { get; init; } = new[] { "iad", "phx", "dub" };

    public IReadOnlyList<string> Namespaces { get; init; } = new[] { "mesh.control", "mesh.payments", "mesh.telemetry" };

    public int NodesPerRegion { get; init; } = 3;

    public int ShardsPerNamespace { get; init; } = 8;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan RenewalLeadTime { get; init; } = TimeSpan.FromMilliseconds(600);

    public TimeSpan EvaluationInterval { get; init; } = TimeSpan.FromMilliseconds(80);

    public TimeSpan ElectionBackoff { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxElectionWindow { get; init; } = TimeSpan.FromSeconds(5);

    public string ClusterId { get; init; } = "hyperscale-mesh";

    public string MeshVersion { get; init; } = "hyperscale-tests";
}
