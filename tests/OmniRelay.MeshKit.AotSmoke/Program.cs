using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Core.Shards.Hashing;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");

builder.Services.AddLogging();
builder.Services.AddGrpc();

builder.Services.AddSingleton<IMeshGossipAgent, NoopMeshGossipAgent>();
builder.Services.AddLeadershipCoordinator();

builder.Services.AddSingleton<IShardRepository, InMemoryShardRepository>();
builder.Services.AddSingleton<ShardHashStrategyRegistry>();
builder.Services.AddSingleton<ShardControlPlaneService>();
builder.Services.AddSingleton<ShardControlGrpcService>();

var app = builder.Build();

app.MapGrpcService<ShardControlGrpcService>();
app.MapGrpcService<LeadershipControlGrpcService>();
app.MapGet("/", () => Results.Ok("meshkit-aot-smoke"));

await WarmUpAsync(app.Services);

await app.StartAsync();
await app.StopAsync();

static async Task WarmUpAsync(IServiceProvider services)
{
    var shardService = services.GetRequiredService<ShardControlPlaneService>();
    var filter = new ShardFilter(namespaceId: "default", ownerNodeId: null, searchShardId: null, statuses: Array.Empty<ShardStatus>());
    _ = (await shardService.ListAsync(filter, cursorToken: null, pageSize: 10, CancellationToken.None).ConfigureAwait(false))
        .ValueOrThrow();

    var simulationRequest = new ShardSimulationRequest
    {
        Namespace = "default",
        Nodes =
        [
            new ShardSimulationNode("node-a", 1, "region-a", "zone-a"),
            new ShardSimulationNode("node-b", 1, "region-b", "zone-b")
        ]
    };

    _ = (await shardService.SimulateAsync(simulationRequest, CancellationToken.None).ConfigureAwait(false))
        .ValueOrThrow();

    var leadership = services.GetRequiredService<LeadershipCoordinator>();
    await leadership.StartAsync().ConfigureAwait(false);
    await leadership.StopAsync().ConfigureAwait(false);
}

internal sealed class InMemoryShardRepository : IShardRepository
{
    private readonly List<ShardRecord> _records = new();
    private readonly List<ShardRecordDiff> _history = new();
    private long _position;
    private readonly object _sync = new();

    public InMemoryShardRepository()
    {
        AddOrUpdate(new ShardMutationRequest
        {
            Namespace = "default",
            ShardId = "shard-0",
            StrategyId = ShardHashStrategyIds.Rendezvous,
            OwnerNodeId = "node-a",
            CapacityHint = 1,
            Status = ShardStatus.Active,
            ChangeMetadata = new ShardChangeMetadata("bootstrap", "seed")
        });
    }

    public ValueTask<ShardRecord?> GetAsync(ShardKey key, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<ShardRecord?>(_records.FirstOrDefault(r => r.Key == key));
        }
    }

    public ValueTask<IReadOnlyList<ShardRecord>> ListAsync(string? namespaceId = null, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var result = namespaceId is null
                ? _records.ToArray()
                : _records.Where(r => string.Equals(r.Namespace, namespaceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            return ValueTask.FromResult<IReadOnlyList<ShardRecord>>(result);
        }
    }

    public ValueTask<ShardMutationResult> UpsertAsync(ShardMutationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var (record, history, created) = AddOrUpdate(request);
            return ValueTask.FromResult(new ShardMutationResult(record, history, created));
        }
    }

    public IAsyncEnumerable<ShardRecordDiff> StreamDiffsAsync(long? sinceVersion, CancellationToken cancellationToken = default)
    {
        return Stream(sinceVersion.GetValueOrDefault(0), cancellationToken);
    }

    public ValueTask<ShardQueryResult> QueryAsync(ShardQueryOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var records = _records
                .Where(r => Matches(options, r))
                .OrderBy(r => r.Namespace, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ShardId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var pageSize = Math.Max(1, options.ResolvePageSize());
            var startIndex = 0;
            if (options.Cursor is not null)
            {
                startIndex = records.FindIndex(r =>
                    string.Equals(r.Namespace, options.Cursor.Namespace, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.ShardId, options.Cursor.ShardId, StringComparison.OrdinalIgnoreCase)) + 1;
                if (startIndex < 0)
                {
                    startIndex = 0;
                }
            }

            var slice = records.Skip(startIndex).Take(pageSize).ToList();
            var nextCursor = slice.Count == pageSize && (startIndex + pageSize) < records.Count
                ? ShardQueryCursor.FromRecord(slice[^1])
                : null;

            var highestVersion = _history.Count == 0 ? 0 : _history[^1].Position;
            return ValueTask.FromResult(new ShardQueryResult(slice, nextCursor, highestVersion));
        }
    }

    private (ShardRecord record, ShardHistoryRecord history, bool created) AddOrUpdate(ShardMutationRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var checksum = ShardChecksum.FromMutation(request);
        var existingIndex = _records.FindIndex(r => r.Key == request.Key);
        var created = existingIndex < 0;
        var version = created ? 1 : _records[existingIndex].Version + 1;

        var record = new ShardRecord
        {
            Namespace = request.Namespace,
            ShardId = request.ShardId,
            StrategyId = request.StrategyId,
            OwnerNodeId = request.OwnerNodeId,
            LeaderId = request.LeaderId,
            CapacityHint = request.CapacityHint,
            Status = request.Status,
            Version = version,
            UpdatedAt = now,
            Checksum = checksum,
            ChangeTicket = request.ChangeTicket
        };

        if (!created && request.ExpectedVersion.HasValue && request.ExpectedVersion.Value != _records[existingIndex].Version)
        {
            throw new ShardConcurrencyException($"Shard '{request.ShardId}' expected version {request.ExpectedVersion}, current {_records[existingIndex].Version}.");
        }

        if (created)
        {
            _records.Add(record);
        }
        else
        {
            _records[existingIndex] = record;
        }

        var history = new ShardHistoryRecord
        {
            Namespace = record.Namespace,
            ShardId = record.ShardId,
            Version = record.Version,
            Actor = request.ChangeMetadata.Actor,
            Reason = request.ChangeMetadata.Reason,
            ChangeTicket = request.ChangeMetadata.ChangeTicket,
            CreatedAt = now,
            OwnerNodeId = record.OwnerNodeId,
            PreviousOwnerNodeId = _history.LastOrDefault()?.Current.OwnerNodeId,
            OwnershipDeltaPercent = request.ChangeMetadata.OwnershipDeltaPercent,
            Metadata = request.ChangeMetadata.Metadata
        };

        var diff = new ShardRecordDiff(++_position, record, created ? null : _history.LastOrDefault()?.Current, history);
        _history.Add(diff);
        return (record, history, created);
    }

    private static bool Matches(ShardQueryOptions options, ShardRecord record)
    {
        if (!string.IsNullOrWhiteSpace(options.Namespace) &&
            !string.Equals(options.Namespace, record.Namespace, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.OwnerNodeId) &&
            !string.Equals(options.OwnerNodeId, record.OwnerNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.SearchShardId) &&
            (record.ShardId?.Contains(options.SearchShardId, StringComparison.OrdinalIgnoreCase) != true))
        {
            return false;
        }

        if (options.Statuses.Count > 0 && !options.Statuses.Contains(record.Status))
        {
            return false;
        }

        return true;
    }

    private async IAsyncEnumerable<ShardRecordDiff> Stream(long since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<ShardRecordDiff> snapshot;
        lock (_sync)
        {
            snapshot = _history.Where(d => d.Position > since).ToList();
        }

        foreach (var diff in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return diff;
            await Task.Yield();
        }
    }
}

internal sealed class NoopMeshGossipAgent : IMeshGossipAgent
{
    private static readonly MeshGossipMemberMetadata Local = new()
    {
        NodeId = "meshkit-aot",
        Role = "control-plane",
        ClusterId = "aot-smoke",
        Region = "local",
        MeshVersion = "smoke"
    };

    public MeshGossipClusterView Snapshot() =>
        new(DateTimeOffset.UtcNow, ImmutableArray<MeshGossipMemberSnapshot>.Empty, Local.NodeId, MeshGossipOptions.CurrentSchemaVersion);

    public MeshGossipMemberMetadata LocalMetadata => Local;

    public bool IsEnabled => true;

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
