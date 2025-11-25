using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.Hashing;
using OmniRelay.ShardStore.Relational;
using OmniRelay.ShardStore.Sqlite;
using Xunit;

namespace OmniRelay.HyperscaleFeatureTests.Scenarios;

public sealed class ShardSchemaHyperscaleFeatureTests : IAsyncLifetime
{
    private readonly SqliteConnection _keepAlive;
    private readonly RelationalShardStore _repository;
    private readonly ShardHashStrategyRegistry _strategies = new();

    public ShardSchemaHyperscaleFeatureTests()
    {
        _keepAlive = new SqliteConnection("Data Source=disc003-hyperscale;Mode=Memory;Cache=Shared");
        _keepAlive.Open();
        InitializeSchema(_keepAlive);
        _repository = SqliteShardStoreFactory.Create(_keepAlive.ConnectionString);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _keepAlive.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact(DisplayName = "Shard hashing stays deterministic while nodes churn across thousands of shards", Timeout = TestTimeouts.Default)]
    public async ValueTask ShardHashing_WithRollingUpdatesRemainsDeterministic()
    {
        var ct = TestContext.Current.CancellationToken;
        var namespaces = new[] { "mesh.control", "mesh.telemetry", "mesh.payments" };
        var initialNodes = CreateNodes();

        foreach (var ns in namespaces)
        {
            var plan = _strategies.Compute(ShardHashStrategyIds.Rendezvous, new ShardHashRequest
            {
                Namespace = ns,
                Nodes = initialNodes,
                Shards = CreateShards(512)
            });

            plan.IsSuccess.ShouldBeTrue();
            await PersistPlanAsync(plan.Value, ct);
        }

        var rollingNodes = initialNodes
            .Where(node => node.NodeId != "iad-zone-2")
            .Append(new ShardNodeDescriptor { NodeId = "iad-zone-3", Region = "iad", Zone = "iad-3", Weight = 1.2 })
            .ToArray();

        foreach (var ns in namespaces)
        {
            var plan = _strategies.Compute(ShardHashStrategyIds.LocalityAware, new ShardHashRequest
            {
                Namespace = ns,
                Nodes = rollingNodes,
                Shards = CreateShards(512, localityHint: ns.Contains("control", StringComparison.OrdinalIgnoreCase) ? "iad" : "phx")
            });

            plan.IsSuccess.ShouldBeTrue();
            await PersistPlanAsync(plan.Value, ct);
        }

        var records = await _repository.ListAsync(cancellationToken: ct);
        records.Count.ShouldBe(namespaces.Length * 512);
        records.Select(r => r.OwnerNodeId).ShouldContain("iad-zone-3");
        records.ShouldAllBe(record => record.Version >= 1);

        var diffs = await ReadDiffsAsync(ct);
        diffs.Count.ShouldBeGreaterThan(1500);
        diffs.Any(diff => diff.Current.OwnerNodeId == "iad-zone-3").ShouldBeTrue();
    }

    [Fact(DisplayName = "Concurrent governance edits surface optimistic concurrency violations with audits", Timeout = TestTimeouts.Default)]
    public async ValueTask ShardRepository_WithConcurrentGovernanceEnforcesOptimism()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = "mesh.governance";
        var seedPlan = _strategies.Compute(ShardHashStrategyIds.ConsistentRing, new ShardHashRequest
        {
            Namespace = ns,
            Nodes = CreateNodes(),
            Shards = CreateShards(256)
        });

        seedPlan.IsSuccess.ShouldBeTrue();
        await PersistPlanAsync(seedPlan.Value, ct);

        var attempts = 0;
        var conflicts = 0;
        var historyTickets = new ConcurrentBag<string?>();

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker * 91 + 3);
            for (var iteration = 0; iteration < 200; iteration++)
            {
                var shardId = random.Next(0, 256).ToString("D4", CultureInfo.InvariantCulture);
                var current = await _repository.GetAsync(new ShardKey(ns, shardId), ct);
                var changeTicket = $"chg-{worker:D2}-{iteration:D3}";
                var request = new ShardMutationRequest
                {
                    Namespace = ns,
                    ShardId = shardId,
                    StrategyId = ShardHashStrategyIds.ConsistentRing,
                    OwnerNodeId = $"node-{worker % 5}",
                    LeaderId = $"node-{worker % 5}",
                    CapacityHint = current?.CapacityHint ?? 1,
                    Status = ShardStatus.Active,
                    ExpectedVersion = current?.Version,
                    ChangeTicket = changeTicket,
                    ChangeMetadata = new ShardChangeMetadata($"operator-{worker}", "governance", changeTicket, random.NextDouble())
                };

                Interlocked.Increment(ref attempts);
                try
                {
                    var result = await _repository.UpsertAsync(request, ct);
                    historyTickets.Add(result.History.ChangeTicket);
                }
                catch (ShardConcurrencyException)
                {
                    Interlocked.Increment(ref conflicts);
                }
            }
        }, ct));

        await Task.WhenAll(workers);

        conflicts.ShouldBeGreaterThan(0);
        attempts.ShouldBeGreaterThan(conflicts);

        var diffs = await ReadDiffsAsync(ct);
        diffs.ShouldNotBeEmpty();
        diffs.Where(diff => diff.History?.Namespace == ns).ShouldNotBeEmpty();
        historyTickets.ShouldContain(ticket => !string.IsNullOrEmpty(ticket));
    }

    private async Task PersistPlanAsync(ShardHashPlan plan, CancellationToken cancellationToken)
    {
        var changeTicket = $"plan-{plan.GeneratedAt.ToUnixTimeSeconds()}";
        var mutations = plan.Assignments
            .Select(assignment => new ShardMutationRequest
            {
                Namespace = assignment.Namespace,
                ShardId = assignment.ShardId,
                StrategyId = plan.StrategyId,
                OwnerNodeId = assignment.OwnerNodeId,
                LeaderId = assignment.OwnerNodeId,
                CapacityHint = assignment.Capacity,
                Status = ShardStatus.Active,
                ChangeTicket = changeTicket,
                ChangeMetadata = new ShardChangeMetadata("planner", "rebalance", changeTicket)
            })
            .Select(request => _repository.UpsertAsync(request, cancellationToken).AsTask());

        await Task.WhenAll(mutations);
    }

    private static IReadOnlyList<ShardNodeDescriptor> CreateNodes() =>
    [
        new ShardNodeDescriptor { NodeId = "iad-zone-1", Region = "iad", Zone = "iad-1", Weight = 1.0 },
        new ShardNodeDescriptor { NodeId = "iad-zone-2", Region = "iad", Zone = "iad-2", Weight = 0.8 },
        new ShardNodeDescriptor { NodeId = "phx-zone-1", Region = "phx", Zone = "phx-1", Weight = 1.3 },
        new ShardNodeDescriptor { NodeId = "phx-zone-2", Region = "phx", Zone = "phx-2", Weight = 1.1 },
        new ShardNodeDescriptor { NodeId = "dub-zone-1", Region = "dub", Zone = "dub-1", Weight = 0.9 },
        new ShardNodeDescriptor { NodeId = "dub-zone-2", Region = "dub", Zone = "dub-2", Weight = 0.9 }
    ];

    private static IReadOnlyList<ShardDefinition> CreateShards(int count, string? localityHint = null)
    {
        return Enumerable.Range(0, count)
            .Select(i => new ShardDefinition
            {
                ShardId = i.ToString("D4", CultureInfo.InvariantCulture),
                LocalityHint = localityHint is null
                    ? (i % 3 == 0 ? "iad/iad-1" : i % 3 == 1 ? "phx/phx-1" : "dub/dub-1")
                    : localityHint,
                Capacity = 1 + (i % 5) * 0.1
            })
            .ToArray();
    }

    private async Task<List<ShardRecordDiff>> ReadDiffsAsync(CancellationToken cancellationToken)
    {
        var diffs = new List<ShardRecordDiff>();
        await foreach (var diff in _repository.StreamDiffsAsync(null, cancellationToken))
        {
            diffs.Add(diff);
        }

        return diffs;
    }

    private static void InitializeSchema(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS shard_records (
    namespace TEXT NOT NULL,
    shard_id TEXT NOT NULL,
    strategy_id TEXT NOT NULL,
    owner_node_id TEXT NOT NULL,
    leader_id TEXT,
    capacity_hint REAL NOT NULL,
    status INTEGER NOT NULL,
    version INTEGER NOT NULL,
    checksum TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    change_ticket TEXT,
    PRIMARY KEY(namespace, shard_id)
);
CREATE TABLE IF NOT EXISTS shard_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    namespace TEXT NOT NULL,
    shard_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    strategy_id TEXT NOT NULL,
    owner_node_id TEXT NOT NULL,
    previous_owner_node_id TEXT,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    change_ticket TEXT,
    ownership_delta_percent REAL,
    metadata TEXT,
    created_at TEXT NOT NULL
);
";
        command.ExecuteNonQuery();
    }
}
