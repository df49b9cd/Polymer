using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.Hashing;
using OmniRelay.ShardStore.Relational;
using OmniRelay.Tests;
using Shouldly;
using Xunit;

namespace OmniRelay.Core.UnitTests.Shards;

public sealed class RelationalShardStoreTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly RelationalShardStore _repository;

    public RelationalShardStoreTests()
    {
        _keepAlive = new SqliteConnection("Data Source=omnirelay-shards;Mode=Memory;Cache=Shared");
        _keepAlive.Open();
        InitializeSchema(_keepAlive);
        _repository = new RelationalShardStore(CreateConnection);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
    }

    [Fact]
    public async Task Upsert_CreatesShardAndLists()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = CreateMutation("mesh.control", "shard-01", "node-a", strategy: ShardHashStrategyIds.ConsistentRing);
        var result = await _repository.UpsertAsync(request, ct);

        result.Created.ShouldBeTrue();
        result.Record.Version.ShouldBe(1);
        result.Record.OwnerNodeId.ShouldBe("node-a");
        result.History.Actor.ShouldBe("operator");

        var fetched = await _repository.GetAsync(new ShardKey("mesh.control", "shard-01"), ct);
        fetched.ShouldNotBeNull();
        fetched!.Version.ShouldBe(1);

        var list = await _repository.ListAsync("mesh.control", ct);
        list.ShouldHaveSingleItem();
        list[0].Checksum.ShouldBe(result.Record.Checksum);
    }

    [Fact]
    public async Task Upsert_UpdatesWithOptimisticConcurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        var insert = CreateMutation("mesh.telemetry", "shard-05", "node-a");
        await _repository.UpsertAsync(insert, ct);

        var update = CreateMutation(
            "mesh.telemetry",
            "shard-05",
            "node-b",
            expectedVersion: 1,
            changeTicket: "chg-002",
            metadata: new ShardChangeMetadata("rebalance-bot", "rebalance", "chg-002", 0.42, "{\"planId\":42}"));

        var result = await _repository.UpsertAsync(update, ct);
        result.Created.ShouldBeFalse();
        result.Record.Version.ShouldBe(2);
        result.Record.OwnerNodeId.ShouldBe("node-b");
        result.History.PreviousOwnerNodeId.ShouldBe("node-a");
        result.History.OwnershipDeltaPercent.ShouldBe(0.42);

        var diffs = await ReadDiffsAsync(cancellationToken: ct);
        diffs.Count.ShouldBe(2);
        diffs.Last().History!.ChangeTicket.ShouldBe("chg-002");
        diffs.Last().Previous!.OwnerNodeId.ShouldBe("node-a");

        var filtered = await ReadDiffsAsync(diffs[0].Position, ct);
        filtered.Count.ShouldBe(1);
        filtered[0].Current.OwnerNodeId.ShouldBe("node-b");
    }

    [Fact]
    public async Task StreamDiffs_ReplaysSnapshotsPerHistoryEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = CreateMutation("mesh.analytics", "shard-09", "node-a");
        await _repository.UpsertAsync(create, ct);

        var update = CreateMutation("mesh.analytics", "shard-09", "node-b", expectedVersion: 1, changeTicket: "chg-analytics");
        await _repository.UpsertAsync(update, ct);

        var diffs = await ReadDiffsAsync(cancellationToken: ct);

        diffs.Count.ShouldBe(2);
        diffs[0].Current.OwnerNodeId.ShouldBe("node-a");
        diffs[0].Current.Version.ShouldBe(1);
        diffs[1].Current.OwnerNodeId.ShouldBe("node-b");
        diffs[1].Current.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Upsert_WithStaleVersionThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var insert = CreateMutation("mesh.payments", "shard-07", "node-a");
        await _repository.UpsertAsync(insert, ct);

        var stale = CreateMutation("mesh.payments", "shard-07", "node-b", expectedVersion: 5);

        await Should.ThrowAsync<ShardConcurrencyException>(async () => await _repository.UpsertAsync(stale, ct));
    }

    private ShardMutationRequest CreateMutation(
        string @namespace,
        string shardId,
        string owner,
        string strategy = ShardHashStrategyIds.Rendezvous,
        long? expectedVersion = null,
        string changeTicket = "chg-001",
        ShardChangeMetadata? metadata = null)
    {
        return new ShardMutationRequest
        {
            Namespace = @namespace,
            ShardId = shardId,
            StrategyId = strategy,
            OwnerNodeId = owner,
            LeaderId = owner,
            CapacityHint = 1,
            Status = ShardStatus.Active,
            ExpectedVersion = expectedVersion,
            ChangeTicket = changeTicket,
            ChangeMetadata = metadata ?? new ShardChangeMetadata("operator", "seed", changeTicket)
        };
    }

    private async Task<List<ShardRecordDiff>> ReadDiffsAsync(long? since = null, CancellationToken cancellationToken = default)
    {
        var list = new List<ShardRecordDiff>();
        await foreach (var diff in _repository.StreamDiffsAsync(since, cancellationToken))
        {
            list.Add(diff);
        }

        return list;
    }

    private DbConnection CreateConnection()
    {
        return new SqliteConnection(_keepAlive.ConnectionString);
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
