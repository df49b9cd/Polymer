using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace OmniRelay.FeatureTests.Fixtures;

/// <summary>
/// Lazily provisions disposable infrastructure dependencies via Testcontainers.
/// </summary>
public sealed class FeatureTestContainers : IAsyncDisposable
{
    private readonly FeatureTestContainerOptions _options;
    private readonly List<IContainer> _managed = new();
    private IContainer? _postgres;
    private IContainer? _eventStore;
    private IContainer? _objectStorage;
    private IContainer? _messageBus;

    public FeatureTestContainers(FeatureTestContainerOptions? options = null)
    {
        _options = options ?? FeatureTestContainerOptions.FromEnvironment();
    }

    public bool ContainersEnabled => _options.ContainersEnabled;

    public bool HasActiveContainers => _managed.Count > 0;

    public string? PostgresConnectionString => _postgres is null
        ? null
        : $"Host=127.0.0.1;Port={_postgres.GetMappedPublicPort(5432)};Username={_options.DatabaseUser};Password={_options.Secrets.PostgresPassword};Database={_options.DatabaseName}";

    public Uri? EventStoreGrpcEndpoint => _eventStore is null
        ? null
        : new Uri($"http://127.0.0.1:{_eventStore.GetMappedPublicPort(2113)}");

    public Uri? ObjectStorageEndpoint => _objectStorage is null
        ? null
        : new Uri($"http://127.0.0.1:{_objectStorage.GetMappedPublicPort(9000)}");

    public Uri? MessageBusEndpoint => _messageBus is null
        ? null
        : new Uri($"nats://127.0.0.1:{_messageBus.GetMappedPublicPort(4222)}");

    public Task<IContainer> EnsurePostgresAsync(CancellationToken cancellationToken = default)
        => EnsureContainerAsync(
            ref _postgres,
            () => new ContainerBuilder()
                .WithImage(_options.PostgresImage)
                .WithName(BuildContainerName("pg"))
                .WithEnvironment("POSTGRES_DB", _options.DatabaseName)
                .WithEnvironment("POSTGRES_USER", _options.DatabaseUser)
                .WithEnvironment("POSTGRES_PASSWORD", _options.Secrets.PostgresPassword)
                .WithPortBinding(5432, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(5432)),
            cancellationToken);

    public Task<IContainer> EnsureEventStoreAsync(CancellationToken cancellationToken = default)
        => EnsureContainerAsync(
            ref _eventStore,
            () => new ContainerBuilder()
                .WithImage(_options.EventStoreImage)
                .WithName(BuildContainerName("eventstore"))
                .WithEnvironment("EVENTSTORE_INSECURE", "true")
                .WithEnvironment("EVENTSTORE_RUN_PROJECTIONS", "All")
                .WithPortBinding(2113, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(2113)),
            cancellationToken);

    public Task<IContainer> EnsureObjectStorageAsync(CancellationToken cancellationToken = default)
        => EnsureContainerAsync(
            ref _objectStorage,
            () => new ContainerBuilder()
                .WithImage(_options.ObjectStorageImage)
                .WithName(BuildContainerName("objectstorage"))
                .WithEnvironment("MINIO_ROOT_USER", _options.Secrets.ObjectStorageAccessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", _options.Secrets.ObjectStorageSecretKey)
                .WithCommand("server", "/data", "--console-address", ":9001")
                .WithPortBinding(9000, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(9000)),
            cancellationToken);

    public Task<IContainer> EnsureMessageBusAsync(CancellationToken cancellationToken = default)
        => EnsureContainerAsync(
            ref _messageBus,
            () => new ContainerBuilder()
                .WithImage(_options.MessageBusImage)
                .WithName(BuildContainerName("nats"))
                .WithPortBinding(4222, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(4222)),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _managed)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }

        _managed.Clear();
        _postgres = null;
        _eventStore = null;
        _objectStorage = null;
        _messageBus = null;
    }

    private Task<IContainer> EnsureContainerAsync(
        ref IContainer? slot,
        Func<ContainerBuilder> configure,
        CancellationToken cancellationToken)
    {
        if (!ContainersEnabled)
        {
            throw new InvalidOperationException("Feature test containers are disabled. Set OMNIRELAY_FEATURETESTS_CONTAINERS=true to enable them.");
        }

        if (slot is not null)
        {
            return Task.FromResult(slot);
        }

        var container = configure()
            .WithCleanUp(true)
            .Build();

        slot = container;
        _managed.Add(container);

        return StartAsync(container, cancellationToken);
    }

    private static async Task<IContainer> StartAsync(IContainer container, CancellationToken cancellationToken)
    {
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        return container;
    }

    private string BuildContainerName(string suffix) => $"omnirelay-featuretests-{suffix}-{_options.SessionId}";
}

public sealed record FeatureTestContainerOptions
{
    public bool ContainersEnabled { get; init; } = !string.Equals(
        Environment.GetEnvironmentVariable("OMNIRELAY_FEATURETESTS_CONTAINERS"),
        "false",
        StringComparison.OrdinalIgnoreCase);

    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");

    public string DatabaseName { get; init; } = "omnirelay";

    public string DatabaseUser { get; init; } = "omnirelay";

    public string PostgresImage { get; init; } = "postgres:16-alpine";

    public string EventStoreImage { get; init; } = "eventstore/eventstore:23.10.0-bionic";

    public string ObjectStorageImage { get; init; } = "minio/minio:RELEASE.2024-08-17T01-24-12Z";

    public string MessageBusImage { get; init; } = "nats:2.10-alpine";

    public FeatureTestContainerSecrets Secrets { get; init; } = FeatureTestContainerSecrets.FromEnvironment();

    public FeatureTestContainerOptions WithContainersEnabled(bool enabled)
        => this with { ContainersEnabled = enabled };

    public static FeatureTestContainerOptions FromEnvironment()
        => new();
}

public sealed record FeatureTestContainerSecrets
{
    public string PostgresPassword { get; init; } =
        Environment.GetEnvironmentVariable("OMNIRELAY_FEATURETESTS_POSTGRES_PASSWORD") ?? "feature-tests";

    public string ObjectStorageAccessKey { get; init; } =
        Environment.GetEnvironmentVariable("OMNIRELAY_FEATURETESTS_OBJECT_STORAGE_ACCESS_KEY") ?? "featuretests";

    public string ObjectStorageSecretKey { get; init; } =
        Environment.GetEnvironmentVariable("OMNIRELAY_FEATURETESTS_OBJECT_STORAGE_SECRET_KEY") ?? "featuretests-secret";

    public static FeatureTestContainerSecrets FromEnvironment() => new();
}
