using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.Hashing;
using OmniRelay.ShardStore.ObjectStorage;
using Xunit;

namespace OmniRelay.Core.UnitTests.Shards;

public sealed class ObjectStorageShardStoreTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Upsert_PersistsInObjectStorage()
    {
        var store = new ObjectStorageShardStore(new InMemoryShardObjectStorage());
        var request = new ShardMutationRequest
        {
            Namespace = "mesh.storage",
            ShardId = "shard-01",
            StrategyId = ShardHashStrategyIds.Rendezvous,
            OwnerNodeId = "node-a",
            LeaderId = "node-a",
            CapacityHint = 1,
            Status = ShardStatus.Active,
            ChangeTicket = "chg-1",
            ChangeMetadata = new ShardChangeMetadata("operator", "seed", "chg-1")
        };

        var result = await store.UpsertAsync(request, TestContext.Current.CancellationToken);
        result.Created.ShouldBeTrue();

        var fetched = await store.GetAsync(new ShardKey("mesh.storage", "shard-01"), TestContext.Current.CancellationToken);
        fetched.ShouldNotBeNull();
        fetched!.OwnerNodeId.ShouldBe("node-a");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Upsert_DetectsConcurrentWrites()
    {
        var store = new ObjectStorageShardStore(new InMemoryShardObjectStorage());
        var request = new ShardMutationRequest
        {
            Namespace = "mesh.storage",
            ShardId = "shard-02",
            StrategyId = ShardHashStrategyIds.Rendezvous,
            OwnerNodeId = "node-a",
            LeaderId = "node-a",
            CapacityHint = 1,
            Status = ShardStatus.Active,
            ChangeMetadata = new ShardChangeMetadata("operator", "seed")
        };

        await store.UpsertAsync(request, TestContext.Current.CancellationToken);

        var stale = new ShardMutationRequest
        {
            Namespace = request.Namespace,
            ShardId = request.ShardId,
            StrategyId = request.StrategyId,
            OwnerNodeId = "node-b",
            LeaderId = "node-b",
            CapacityHint = request.CapacityHint,
            Status = request.Status,
            ExpectedVersion = 5,
            ChangeMetadata = new ShardChangeMetadata("operator", "seed")
        };

        await Should.ThrowAsync<ShardConcurrencyException>(async () => await store.UpsertAsync(stale, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StreamDiffs_ReturnsMutations()
    {
        var store = new ObjectStorageShardStore(new InMemoryShardObjectStorage());
        for (var i = 0; i < 3; i++)
        {
            var request = new ShardMutationRequest
            {
                Namespace = "mesh.storage",
                ShardId = "shard-03",
                StrategyId = ShardHashStrategyIds.Rendezvous,
                OwnerNodeId = $"node-{i}",
                LeaderId = $"node-{i}",
                CapacityHint = 1,
                Status = ShardStatus.Active,
                ChangeMetadata = new ShardChangeMetadata("operator", "update")
            };

            await store.UpsertAsync(request, TestContext.Current.CancellationToken);
        }

        var diffs = new List<ShardRecordDiff>();
        await foreach (var diff in store.StreamDiffsAsync(null, TestContext.Current.CancellationToken))
        {
            diffs.Add(diff);
        }

        diffs.Count.ShouldBe(3);
        diffs.Last().Current.OwnerNodeId.ShouldBe("node-2");
    }
}
