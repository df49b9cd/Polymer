using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hugo;

namespace OmniRelay.Dispatcher;

/// <summary>Coordinates deterministic capture of resource lease replication events.</summary>
public interface IResourceLeaseDeterministicCoordinator
{
    ValueTask RecordAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}

/// <summary>Options needed to wire a <see cref="DeterministicResourceLeaseCoordinator"/>.</summary>
public sealed class ResourceLeaseDeterministicOptions
{
    /// <summary>The deterministic state store backing VersionGate and DeterministicEffectStore.</summary>
    public IDeterministicStateStore StateStore { get; init; } = new InMemoryDeterministicStateStore();

    /// <summary>Logical change identifier recorded in VersionGate.</summary>
    public string ChangeId { get; init; } = "resourcelease.replication";

    /// <summary>Minimum supported change version.</summary>
    public int MinVersion { get; init; } = 1;

    /// <summary>Maximum supported change version.</summary>
    public int MaxVersion { get; init; } = 1;

    /// <summary>Optional factory that customizes the deterministic effect identifier per event.</summary>
    public Func<ResourceLeaseReplicationEvent, string>? EffectIdFactory { get; init; }

    /// <summary>Serializer context used to persist deterministic helper types (defaults to <see cref="Hugo.DeterministicJsonSerialization.DefaultContext"/>).</summary>
    public JsonSerializerContext? SerializerContext { get; init; } = DeterministicJsonSerialization.DefaultContext;

    /// <summary>Overrides serializer options used for deterministic persistence.</summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }
}

/// <summary>
/// Default deterministic coordinator that captures each replication event via <see cref="DeterministicEffectStore"/> and
/// <see cref="DeterministicGate"/> so failovers can replay the exact sequence without duplication.
/// </summary>
public sealed class DeterministicResourceLeaseCoordinator : IResourceLeaseDeterministicCoordinator
{
    private readonly DeterministicEffectStore _effectStore;
    private readonly DeterministicGate _gate;
    private readonly string _changeId;
    private readonly int _minVersion;
    private readonly int _maxVersion;
    private readonly Func<ResourceLeaseReplicationEvent, string> _effectIdFactory;

    public DeterministicResourceLeaseCoordinator(ResourceLeaseDeterministicOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stateStore = options.StateStore ?? throw new ArgumentException("StateStore is required for deterministic coordination.", nameof(options));

        var serializerOptions = CreateSerializerOptions(options);
        var serializerContext = ResolveSerializerContext(options, serializerOptions);

        _effectStore = new DeterministicEffectStore(
            stateStore,
            serializerOptions: serializerOptions,
            serializerContext: serializerContext);

        var versionGate = new VersionGate(
            stateStore,
            serializerOptions: serializerOptions,
            serializerContext: serializerContext);

        _gate = new DeterministicGate(versionGate, _effectStore);
        _changeId = string.IsNullOrWhiteSpace(options.ChangeId) ? "resourcelease.replication" : options.ChangeId!;
        _minVersion = Math.Max(1, options.MinVersion);
        _maxVersion = Math.Max(_minVersion, options.MaxVersion);
        _effectIdFactory = options.EffectIdFactory ?? (evt => $"{_changeId}/seq/{evt.SequenceNumber}");
    }

    public async ValueTask RecordAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        var changeScope = $"{_changeId}.{replicationEvent.SequenceNumber}";
        var effectId = _effectIdFactory(replicationEvent);

        var result = await _gate.ExecuteAsync(
            changeScope,
            _minVersion,
            _maxVersion,
            (_, ct) => CaptureEffectAsync(effectId, replicationEvent, ct),
            _ => _maxVersion,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }
    }

    private async Task<Result<long>> CaptureEffectAsync(string effectId, ResourceLeaseReplicationEvent evt, CancellationToken cancellationToken) =>
        await _effectStore.CaptureAsync<long>(
            effectId,
            _ => Task.FromResult(Result.Ok(evt.SequenceNumber)),
            cancellationToken).ConfigureAwait(false);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Deterministic effect payloads may require runtime metadata.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Deterministic effect payloads may require runtime metadata.")]
    private static JsonSerializerOptions CreateSerializerOptions(ResourceLeaseDeterministicOptions options)
    {
        var serializerOptions = options.SerializerOptions is not null
            ? new JsonSerializerOptions(options.SerializerOptions)
            : new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var runtimeResolver = new DefaultJsonTypeInfoResolver();
        serializerOptions.TypeInfoResolver = serializerOptions.TypeInfoResolver is null
            ? runtimeResolver
            : JsonTypeInfoResolver.Combine(serializerOptions.TypeInfoResolver, runtimeResolver);

        return serializerOptions;
    }

    private static JsonSerializerContext ResolveSerializerContext(ResourceLeaseDeterministicOptions options, JsonSerializerOptions serializerOptions)
    {
        if (options.SerializerContext is not null)
        {
            return options.SerializerContext;
        }

        if (options.SerializerOptions is not null)
        {
            return DeterministicJsonSerialization.CreateContext(serializerOptions);
        }

        return DeterministicJsonSerialization.DefaultContext;
    }
}
