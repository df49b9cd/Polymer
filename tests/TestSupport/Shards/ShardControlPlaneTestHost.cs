using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Diagnostics;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Core.Shards.Hashing;
using OmniRelay.Diagnostics;
using OmniRelay.ShardStore.Relational;

namespace OmniRelay.Tests.Support;

public sealed class ShardControlPlaneTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SqliteConnection _connection;

    private ShardControlPlaneTestHost(WebApplication app, SqliteConnection connection, RelationalShardStore repository, Uri baseAddress)
    {
        _app = app;
        _connection = connection;
        Repository = repository;
        BaseAddress = baseAddress;
    }

    public RelationalShardStore Repository { get; }

    public Uri BaseAddress { get; }

    public static async Task<ShardControlPlaneTestHost> StartAsync(CancellationToken cancellationToken = default)
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var keepAlive = new SqliteConnection($"Data Source=shard-control-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await keepAlive.OpenAsync(cancellationToken).ConfigureAwait(false);
        InitializeSchema(keepAlive);
        var repository = new RelationalShardStore(() => new SqliteConnection(keepAlive.ConnectionString));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        builder.Services.AddLogging(logging => logging.AddSimpleConsole());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IDiagnosticsRuntime, TestDiagnosticsRuntime>();
        builder.Services.AddSingleton<IMeshGossipAgent, TestMeshGossipAgent>();
        builder.Services.AddSingleton<NodeDrainCoordinator>();
        builder.Services.AddSingleton<IShardRepository>(repository);
        builder.Services.AddSingleton<ShardHashStrategyRegistry>();
        builder.Services.AddSingleton<ShardControlPlaneService>();
        builder.Services.AddSingleton<ShardControlGrpcService>();

        var app = builder.Build();
        app.MapShardDiagnosticsEndpoints();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        return new ShardControlPlaneTestHost(app, keepAlive, repository, baseAddress);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask SeedAsync(IEnumerable<ShardMutationRequest> mutations, CancellationToken cancellationToken = default)
    {
        foreach (var mutation in mutations)
        {
            await Repository.UpsertAsync(mutation, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void InitializeSchema(SqliteConnection connection)
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

    private sealed class TestDiagnosticsRuntime : IDiagnosticsRuntime
    {
        public LogLevel? MinimumLogLevel { get; private set; }

        public double? TraceSamplingProbability { get; private set; }

        public void SetMinimumLogLevel(LogLevel? level) => MinimumLogLevel = level;

        public void SetTraceSamplingProbability(double? probability) => TraceSamplingProbability = probability;
    }

    private sealed class TestMeshGossipAgent : IMeshGossipAgent
    {
        public MeshGossipClusterView Snapshot() => new(DateTimeOffset.UtcNow, ImmutableArray<MeshGossipMemberSnapshot>.Empty, "test", "1.0");

        public MeshGossipMemberMetadata LocalMetadata { get; } = new();

        public bool IsEnabled => true;

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
