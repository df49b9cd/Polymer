using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OmniRelay.Core.Peers;
using OmniRelay.Dispatcher;
using OmniRelay.Samples.ResourceLease.MeshDemo;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "MESHDEMO_");

var meshDemoSection = builder.Configuration.GetSection("meshDemo");
var bootstrapOptions = new MeshDemoOptions();
meshDemoSection.Bind(bootstrapOptions);
var activeRoles = MeshDemoRoleExtensions.ResolveRoles(bootstrapOptions.Roles);

if (activeRoles.HasRole(MeshDemoRole.Diagnostics) && !activeRoles.HasRole(MeshDemoRole.Dispatcher))
{
    throw new InvalidOperationException("Diagnostics role requires the dispatcher role.");
}

builder.Services.Configure<MeshDemoOptions>(meshDemoSection);
ConfigureMeshMetrics(builder, bootstrapOptions);

if (activeRoles.HasRole(MeshDemoRole.Dispatcher))
{
    builder.Services.AddSingleton(sp =>
    {
        var options = sp.GetRequiredService<IOptions<MeshDemoOptions>>().Value;
        return MeshDemoPaths.Create(options);
    });

    builder.Services.AddSingleton<PeerLeaseHealthTracker>();
    builder.Services.AddSingleton<BackpressureAwareRateLimiter>(sp =>
    {
        var limiter = new BackpressureAwareRateLimiter(
            normalLimiter: BackpressureLimiterFactory.Create(permitLimit: 32),
            backpressureLimiter: BackpressureLimiterFactory.Create(permitLimit: 4));
        return limiter;
    });

    if (activeRoles.HasRole(MeshDemoRole.Diagnostics))
    {
        builder.Services.AddSingleton<ResourceLeaseBackpressureDiagnosticsListener>();
        builder.Services.AddSingleton<IResourceLeaseBackpressureListener>(sp =>
            sp.GetRequiredService<ResourceLeaseBackpressureDiagnosticsListener>());
        builder.Services.AddSingleton<MeshReplicationLog>();
        builder.Services.AddSingleton<IResourceLeaseReplicationSink>(sp =>
            new MeshReplicationLogSink(sp.GetRequiredService<MeshReplicationLog>()));
    }

    builder.Services.AddSingleton<IResourceLeaseBackpressureListener>(sp =>
        new RateLimitingBackpressureListener(
            sp.GetRequiredService<BackpressureAwareRateLimiter>(),
            sp.GetRequiredService<ILogger<RateLimitingBackpressureListener>>()));

    builder.Services.AddSingleton(sp =>
    {
        var paths = sp.GetRequiredService<MeshDemoPaths>();
        var sinks = sp.GetServices<IResourceLeaseReplicationSink>();
        return new SqliteResourceLeaseReplicator(paths.ReplicationConnectionString, tableName: "LeaseEvents", sinks: sinks);
    });

    builder.Services.AddSingleton(sp =>
    {
        var paths = sp.GetRequiredService<MeshDemoPaths>();
        return new SqliteDeterministicStateStore(paths.DeterministicConnectionString);
    });

    builder.Services.AddSingleton<MeshDispatcherHostedService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MeshDispatcherHostedService>());
}

if (activeRoles.HasRole(MeshDemoRole.Seeder) ||
    activeRoles.HasRole(MeshDemoRole.Worker) ||
    activeRoles.HasRole(MeshDemoRole.Diagnostics))
{
    builder.Services.AddHttpClient<ResourceLeaseHttpClient>((sp, httpClient) =>
    {
        var options = sp.GetRequiredService<IOptions<MeshDemoOptions>>().Value;
        httpClient.BaseAddress = options.GetRpcBaseUri();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
    });
}

if (activeRoles.HasRole(MeshDemoRole.Seeder))
{
    builder.Services.AddHostedService<LeaseSeederHostedService>();
}

if (activeRoles.HasRole(MeshDemoRole.Worker))
{
    builder.Services.AddHostedService<LeaseWorkerHostedService>();
}

var app = builder.Build();

app.MapGet("/", (IOptions<MeshDemoOptions> optionsAccessor) =>
{
    var banner = BuildBanner(optionsAccessor.Value, activeRoles);
    return Results.Text(banner);
});

if (activeRoles.HasRole(MeshDemoRole.Diagnostics))
{
    app.MapPost("/demo/enqueue", async (MeshEnqueueRequest request, ResourceLeaseHttpClient client, CancellationToken ct) =>
    {
        var payload = request.ToPayload();
        var response = await client.EnqueueAsync(payload, ct).ConfigureAwait(false);
        return Results.Json(response, MeshJson.Options);
    });

    app.MapGet("/demo/lease-health", (PeerLeaseHealthTracker tracker) =>
    {
        var snapshots = tracker.Snapshot();
        return Results.Json(PeerLeaseHealthDiagnostics.FromSnapshots(snapshots), MeshJson.Options);
    });

    app.MapGet("/demo/backpressure", (ResourceLeaseBackpressureDiagnosticsListener listener) =>
    {
        return listener.Latest is { } latest
            ? Results.Json(latest, MeshJson.Options)
            : Results.NoContent();
    });

    app.MapGet("/demo/replication", (MeshReplicationLog log) =>
    {
        return Results.Json(log.GetRecent(), MeshJson.Options);
    });
}

app.MapPrometheusScrapingEndpoint("/metrics");
app.Run();

static string BuildBanner(MeshDemoOptions options, MeshDemoRole roles)
{
    var sb = new StringBuilder();
    sb.AppendLine("OmniRelay ResourceLease Mesh Demo");
    sb.AppendLine();
    sb.AppendLine($"Active roles: {roles}");
    sb.AppendLine();

    if (roles.HasRole(MeshDemoRole.Diagnostics))
    {
        sb.AppendLine("Key endpoints:");
        sb.AppendLine("- POST /demo/enqueue            -> enqueue sample work items without CLI");
        sb.AppendLine("- GET  /demo/lease-health       -> PeerLeaseHealthTracker snapshot (JSON)");
        sb.AppendLine("- GET  /demo/backpressure       -> Last backpressure signal (JSON)");
        sb.AppendLine("- GET  /demo/replication        -> Recent replication events (JSON)");
        sb.AppendLine();
    }
    else
    {
        sb.AppendLine("Diagnostics endpoints are disabled on this node.");
        sb.AppendLine();
    }

    var rpcBase = (options.RpcUrl ?? "http://127.0.0.1:7420").TrimEnd('/');
    var ns = string.IsNullOrWhiteSpace(options.Namespace) ? "resourcelease.mesh" : options.Namespace;
    sb.AppendLine("RPC endpoints:");
    sb.AppendLine($"- ResourceLease dispatcher listens on {rpcBase}/yarpc/v1 (namespace {ns})");
    sb.AppendLine();
    sb.AppendLine("Try commands:");
    sb.AppendLine($"  omnirelay request --transport http --url {rpcBase}/yarpc/v1 \\");
    sb.AppendLine($"    --service {options.ServiceName ?? "resourcelease-mesh-demo"} \\");
    sb.AppendLine($"    --procedure {ns}::enqueue \\");
    sb.AppendLine("    --encoding application/json \\");
    sb.AppendLine("    --body '{\"payload\":{\"resourceType\":\"demo\",\"resourceId\":\"cli\",\"partitionKey\":\"cli\",\"payloadEncoding\":\"json\",\"body\":\"eyJtZXNzYWdlIjoiY2xpIn0=\"}}'");
    return sb.ToString();
}

static void ConfigureMeshMetrics(WebApplicationBuilder builder, MeshDemoOptions bootstrap)
{
    var resource = ResourceBuilder.CreateDefault()
        .AddService(bootstrap.ServiceName ?? "resourcelease-mesh-demo");

    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics =>
        {
            metrics
                .SetResourceBuilder(resource)
                .AddRuntimeInstrumentation()
                .AddMeter(
                    "OmniRelay.Dispatcher.ResourceLease",
                    "OmniRelay.Dispatcher.ResourceLeaseReplication",
                    "OmniRelay.Core.Peers",
                    "OmniRelay.Transport.Http")
                .AddPrometheusExporter();
        });
}
