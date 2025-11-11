using System.Text;
using Microsoft.Extensions.Options;
using OmniRelay.Core.Peers;
using OmniRelay.Dispatcher;
using OmniRelay.Samples.ResourceLease.MeshDemo;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "MESHDEMO_");

var meshDemoSection = builder.Configuration.GetSection("meshDemo");
var bootstrapOptions = new MeshDemoOptions();
meshDemoSection.Bind(bootstrapOptions);
var activeRoles = MeshDemoRoleExtensions.ResolveRoles(bootstrapOptions.Roles);
var hostingUrls = bootstrapOptions.GetHostingUrls();
if (hostingUrls.Length > 0)
{
    builder.WebHost.UseUrls(hostingUrls);
}

if (activeRoles.HasRole(MeshDemoRole.Diagnostics) && !activeRoles.HasRole(MeshDemoRole.Dispatcher))
{
    throw new InvalidOperationException("Diagnostics role requires the dispatcher role.");
}

builder.Services.Configure<MeshDemoOptions>(meshDemoSection);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, MeshJsonContext.Default);
});
builder.Services.AddSingleton<LakehouseCatalogState>();
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

    builder.Services.AddSingleton<IResourceLeaseReplicator>(sp =>
    {
        var sinks = sp.GetServices<IResourceLeaseReplicationSink>();
        return new InMemoryResourceLeaseReplicator(sinks);
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
    builder.Services.AddHostedService<LakehouseCatalogSeederHostedService>();
}

if (activeRoles.HasRole(MeshDemoRole.Worker))
{
    builder.Services.AddHostedService<LakehouseCatalogWorkerHostedService>();
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
        return TypedResults.Json(response, ResourceLeaseJson.Context.ResourceLeaseEnqueueResponse);
    });

    app.MapGet("/demo/lease-health", (PeerLeaseHealthTracker tracker) =>
    {
        var snapshots = tracker.Snapshot();
        return TypedResults.Json(PeerLeaseHealthDiagnostics.FromSnapshots(snapshots), MeshJson.Context.PeerLeaseHealthDiagnostics);
    });

    app.MapGet("/demo/backpressure", (ResourceLeaseBackpressureDiagnosticsListener listener) =>
    {
        return listener.Latest is { } latest
            ? TypedResults.Json(latest, MeshJson.Context.ResourceLeaseBackpressureSignal)
            : Results.NoContent();
    });

    app.MapGet("/demo/replication", (MeshReplicationLog log) =>
    {
        var recent = log.GetRecent().ToArray();
        return TypedResults.Json(recent, ResourceLeaseJson.Context.ResourceLeaseReplicationEventArray);
    });

    app.MapGet("/demo/catalogs", (LakehouseCatalogState catalogState) =>
    {
        var snapshot = catalogState.Snapshot();
        return TypedResults.Json(snapshot, MeshJson.Context.LakehouseCatalogSnapshot);
    });
}

app.MapPrometheusScrapingEndpoint("/metrics");
app.Run();

static string BuildBanner(MeshDemoOptions options, MeshDemoRole roles)
{
    var sb = new StringBuilder();
    sb.AppendLine("OmniRelay ResourceLease Mesh Demo");
    sb.AppendLine();
    sb.AppendLine(FormattableString.Invariant($"Active roles: {roles}"));
    sb.AppendLine();

    if (roles.HasRole(MeshDemoRole.Diagnostics))
    {
        sb.AppendLine("Key endpoints:");
        sb.AppendLine("- POST /demo/enqueue            -> enqueue catalog mutations without CLI");
        sb.AppendLine("- GET  /demo/lease-health       -> PeerLeaseHealthTracker snapshot (JSON)");
        sb.AppendLine("- GET  /demo/backpressure       -> Last backpressure signal (JSON)");
        sb.AppendLine("- GET  /demo/catalogs           -> Lakehouse catalog snapshot (JSON)");
        sb.AppendLine("- GET  /demo/replication        -> Recent replication events (JSON)");
        sb.AppendLine();
    }
    else
    {
        sb.AppendLine("Diagnostics endpoints are disabled on this node.");
        sb.AppendLine();
    }

    var rpcBase = (options.RpcUrl ?? "http://127.0.0.1:7421").TrimEnd('/');
    var rpcEndpoint = $"{rpcBase}/omnirelay/yarpc/v1";
    var ns = string.IsNullOrWhiteSpace(options.Namespace) ? "resourcelease.mesh" : options.Namespace;
    sb.AppendLine("RPC endpoints:");
    sb.AppendLine(FormattableString.Invariant($"- ResourceLease dispatcher listens on {rpcEndpoint} (namespace {ns})"));
    sb.AppendLine();
    sb.AppendLine("Try commands:");
    sb.AppendLine(FormattableString.Invariant($"  omnirelay request --transport http --url {rpcEndpoint} \\"));
    sb.AppendLine(FormattableString.Invariant($"    --service {options.ServiceName ?? "resourcelease-mesh-demo"} \\"));
    sb.AppendLine(FormattableString.Invariant($"    --procedure {ns}::enqueue \\"));
    sb.AppendLine("    --encoding application/json \\");
    sb.AppendLine("    --body '{\"payload\":{\"resourceType\":\"lakehouse.catalog\",\"resourceId\":\"fabric-lakehouse.sales.orders.v0042\",\"partitionKey\":\"fabric-lakehouse\",\"payloadEncoding\":\"application/json\",\"body\":\"eyJjYXRhbG9nIjogImZhYnJpYy1sYWtlaG91c2UiLCAiZGF0YWJhc2UiOiAic2FsZXMiLCAidGFibGUiOiAib3JkZXJzIiwgIm9wZXJhdGlvblR5cGUiOiAiQ29tbWl0U25hcHNob3QiLCAidmVyc2lvbiI6IDQyLCAicHJpbmNpcGFsIjogInNwYXJrLWNsaSIsICJjb2x1bW5zIjogWyJpZCBTVFJJTkciLCAicGF5bG9hZCBTVFJJTkciXSwgImNoYW5nZXMiOiBbImNvbW1pdCBzbmFwc2hvdCBmcm9tIENMSSJdLCAic25hcHNob3RJZCI6ICJjbGktc25hcHNob3QiLCAidGltZXN0YW1wIjogIjIwMjQtMDEtMDFUMDA6MDA6MDBaIiwgInJlcXVlc3RJZCI6ICJjbGkifQ==\"}}'");
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
