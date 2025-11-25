using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Hugo;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Core.Shards.Hashing;
using Xunit;

namespace OmniRelay.Core.UnitTests.Shards.ControlPlane;

public sealed class ShardControlPlaneServiceTests
{
    [Fact]
    public async Task Simulate_FanOut_MergesChanges()
    {
        var records = new[]
        {
            new ShardRecord
            {
                Namespace = "ns",
                ShardId = "shard-a",
                StrategyId = ShardHashStrategyIds.Rendezvous,
                OwnerNodeId = "node-a",
                CapacityHint = 1,
                Version = 1,
                Checksum = "c1",
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new ShardRecord
            {
                Namespace = "ns",
                ShardId = "shard-b",
                StrategyId = ShardHashStrategyIds.Rendezvous,
                OwnerNodeId = "node-b",
                CapacityHint = 1,
                Version = 1,
                Checksum = "c2",
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        var plannedAssignments = new[]
        {
            new ShardAssignment { Namespace = "ns", ShardId = "shard-a", OwnerNodeId = "node-c" },
            new ShardAssignment { Namespace = "ns", ShardId = "shard-b", OwnerNodeId = "node-b" }
        };

        var strategy = new TestStrategy("fanout-test", plannedAssignments);
        var registry = new ShardHashStrategyRegistry([strategy]);
        var repository = new FakeShardRepository(records);
        var service = new ShardControlPlaneService(repository, registry, TimeProvider.System, NullLogger<ShardControlPlaneService>.Instance);

        var request = new ShardSimulationRequest
        {
            Namespace = "ns",
            StrategyId = strategy.Id,
            Nodes = [new ShardSimulationNode("node-a", 1, null, null)]
        };

        var result = await service.SimulateAsync(request, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Assignments.Count.ShouldBe(2);
        result.Value.Changes.ShouldHaveSingleItem();
        var change = result.Value.Changes[0];
        change.ShardId.ShouldBe("shard-a");
        change.CurrentOwner.ShouldBe("node-a");
        change.ProposedOwner.ShouldBe("node-c");
        change.ChangesOwner.ShouldBeTrue();
    }

    [Fact]
    public async Task Simulate_FanOut_Fails_WhenWorkerFails()
    {
        var records = new[]
        {
            new ShardRecord
            {
                Namespace = "ns",
                ShardId = "shard-a",
                StrategyId = ShardHashStrategyIds.Rendezvous,
                OwnerNodeId = "node-a",
                CapacityHint = 1,
                Version = 1,
                Checksum = "c1",
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new ShardRecord
            {
                Namespace = "ns",
                ShardId = "shard-b",
                StrategyId = ShardHashStrategyIds.Rendezvous,
                OwnerNodeId = "node-b",
                CapacityHint = 1,
                Version = 1,
                Checksum = "c2",
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        var plannedAssignments = new[]
        {
            new ShardAssignment { Namespace = "ns", ShardId = "shard-a", OwnerNodeId = "node-b" },
            new ShardAssignment { Namespace = "ns", ShardId = "shard-b", OwnerNodeId = "node-c", LocalityHint = "fail" }
        };

        var strategy = new TestStrategy("fanout-fail", plannedAssignments);
        var registry = new ShardHashStrategyRegistry([strategy]);
        var repository = new FakeShardRepository(records);
        var service = new ShardControlPlaneService(repository, registry, TimeProvider.System, NullLogger<ShardControlPlaneService>.Instance);

        var request = new ShardSimulationRequest
        {
            Namespace = "ns",
            StrategyId = strategy.Id,
            Nodes = [new ShardSimulationNode("node-a", 1, null, null)]
        };

        var result = await service.SimulateAsync(request, CancellationToken.None);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("shards.control.assignment.failed");
    }

    [Fact]
    public async Task Simulate_Fails_WhenAssignmentMissing()
    {
        var records = new[]
        {
            new ShardRecord
            {
                Namespace = "ns",
                ShardId = "shard-a",
                StrategyId = ShardHashStrategyIds.Rendezvous,
                OwnerNodeId = "node-a",
                CapacityHint = 1,
                Version = 1,
                Checksum = "c1",
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        var plannedAssignments = new[]
        {
            new ShardAssignment { Namespace = "ns", ShardId = "shard-a", OwnerNodeId = "node-b" },
            new ShardAssignment { Namespace = "ns", ShardId = "missing", OwnerNodeId = "node-c" }
        };

        var strategy = new TestStrategy("fanout-missing", plannedAssignments);
        var registry = new ShardHashStrategyRegistry([strategy]);
        var repository = new FakeShardRepository(records);
        var service = new ShardControlPlaneService(repository, registry, TimeProvider.System, NullLogger<ShardControlPlaneService>.Instance);

        var request = new ShardSimulationRequest
        {
            Namespace = "ns",
            StrategyId = strategy.Id,
            Nodes = [new ShardSimulationNode("node-a", 1, null, null)]
        };

        var result = await service.SimulateAsync(request, CancellationToken.None);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("shards.control.assignment.missing");
    }

    [Fact]
    public async Task CollectWatchAsync_AggregatesFailures()
    {
        var current = new ShardRecord
        {
            Namespace = "ns",
            ShardId = "shard-a",
            StrategyId = ShardHashStrategyIds.Rendezvous,
            OwnerNodeId = "node-a",
            CapacityHint = 1,
            Version = 1,
            Checksum = "c1",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var diffs = new[]
        {
            new ShardRecordDiff(1, current, null)
        };

        var repository = new FakeStreamRepository(diffs, throwAfter: true);
        var registry = new ShardHashStrategyRegistry();
        var service = new ShardControlPlaneService(repository, registry, TimeProvider.System, NullLogger<ShardControlPlaneService>.Instance);
        var filter = new ShardFilter("ns", null, null, null);

        var result = await service.CollectWatchAsync(null, filter, CancellationToken.None);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("shards.control.stream.failure");
    }

    private sealed class FakeShardRepository : IShardRepository
    {
        private readonly IReadOnlyList<ShardRecord> _records;

        public FakeShardRepository(IReadOnlyList<ShardRecord> records)
        {
            _records = records;
        }

        public ValueTask<ShardRecord?> GetAsync(ShardKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ShardRecord?>(_records.FirstOrDefault(r => r.Key == key));

        public ValueTask<IReadOnlyList<ShardRecord>> ListAsync(string? namespaceId = null, CancellationToken cancellationToken = default)
        {
            var filtered = string.IsNullOrWhiteSpace(namespaceId)
                ? _records
                : _records.Where(r => string.Equals(r.Namespace, namespaceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            return ValueTask.FromResult<IReadOnlyList<ShardRecord>>(filtered);
        }

        public ValueTask<ShardMutationResult> UpsertAsync(ShardMutationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ShardRecordDiff> StreamDiffsAsync(long? sinceVersion, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ShardRecordDiff>();

        public ValueTask<ShardQueryResult> QueryAsync(ShardQueryOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeStreamRepository : IShardRepository
    {
        private readonly IReadOnlyList<ShardRecordDiff> _diffs;
        private readonly bool _throwAfter;

        public FakeStreamRepository(IReadOnlyList<ShardRecordDiff> diffs, bool throwAfter)
        {
            _diffs = diffs;
            _throwAfter = throwAfter;
        }

        public ValueTask<ShardRecord?> GetAsync(ShardKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ShardRecord?>(null);

        public ValueTask<IReadOnlyList<ShardRecord>> ListAsync(string? namespaceId = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ShardRecord>>(Array.Empty<ShardRecord>());

        public ValueTask<ShardMutationResult> UpsertAsync(ShardMutationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ShardRecordDiff> StreamDiffsAsync(long? sinceVersion, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var diff in _diffs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return diff;
            }

            if (_throwAfter)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("stream failure");
            }
        }

        public ValueTask<ShardQueryResult> QueryAsync(ShardQueryOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestStrategy : IShardHashStrategy
    {
        private readonly IReadOnlyList<ShardAssignment> _assignments;

        public TestStrategy(string id, IReadOnlyList<ShardAssignment> assignments)
        {
            Id = id;
            _assignments = assignments;
        }

        public string Id { get; }

        public Result<ShardHashPlan> Compute(ShardHashRequest request)
        {
            return Result.Ok(new ShardHashPlan(
                request.Namespace ?? "ns",
                Id,
                _assignments,
                DateTimeOffset.UtcNow));
        }
    }
}
