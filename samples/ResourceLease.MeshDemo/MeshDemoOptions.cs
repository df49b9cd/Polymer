namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal sealed class MeshDemoOptions
{
    public static readonly string[] DefaultCatalogs = ["fabric-lakehouse", "delta-lab", "governance-hub"];
    public static readonly string[] DefaultDatabasePrefixes = ["sales", "billing", "ml", "governance", "security"];
    public static readonly string[] DefaultPrincipals = ["spark-streaming", "trino-bi", "governor", "fabric-sync", "delta-maintenance"];

    public string ServiceName { get; set; } = "resourcelease-mesh-demo";

    public string Namespace { get; set; } = "resourcelease.mesh";

    public string RpcUrl { get; set; } = "http://127.0.0.1:7421";

    public string? RpcClientUrl { get; set; }

    public string DataDirectory { get; set; } = "mesh-data";

    public string WorkerPeerId { get; set; } = $"mesh-worker-{Environment.MachineName}";

    public double SeederIntervalSeconds { get; set; } = 5;

    public string[] Roles { get; set; } =
    [
        nameof(MeshDemoRole.Dispatcher),
        nameof(MeshDemoRole.Seeder),
        nameof(MeshDemoRole.Worker),
        nameof(MeshDemoRole.Diagnostics)
    ];

    public string[] Catalogs { get; set; } = DefaultCatalogs;

    public string[] DatabasePrefixes { get; set; } = DefaultDatabasePrefixes;

    public string[] Principals { get; set; } = DefaultPrincipals;

    public string[]? Urls { get; set; }

    public Uri GetRpcBaseUri()
    {
        var clientUri = string.IsNullOrWhiteSpace(RpcClientUrl) ? RpcUrl : RpcClientUrl!;
        var baseUri = clientUri?.TrimEnd('/') ?? "http://127.0.0.1:7420";
        return new Uri($"{baseUri}/omnirelay/v1/resourcelease.mesh", UriKind.Absolute);
    }

    public string[] GetHostingUrls() =>
        Urls is { Length: > 0 } values ? values : [];

    public TimeSpan GetSeederInterval() =>
        TimeSpan.FromSeconds(Math.Max(1, SeederIntervalSeconds));
}

internal sealed class MeshDemoPaths
{
    private MeshDemoPaths(string replicationConnectionString, string deterministicConnectionString)
    {
        ReplicationConnectionString = replicationConnectionString;
        DeterministicConnectionString = deterministicConnectionString;
    }

    public string ReplicationConnectionString { get; }

    public string DeterministicConnectionString { get; }

    public static MeshDemoPaths Create(MeshDemoOptions options)
    {
        var path = options.DataDirectory;
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }

        Directory.CreateDirectory(path);
        var replicationPath = Path.Combine(path, "replication.db");
        var deterministicPath = Path.Combine(path, "deterministic.db");
        return new MeshDemoPaths(
            replicationConnectionString: $"Data Source={replicationPath};Cache=Shared",
            deterministicConnectionString: $"Data Source={deterministicPath};Cache=Shared");
    }
}
