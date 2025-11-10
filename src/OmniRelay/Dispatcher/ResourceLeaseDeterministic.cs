using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public IDeterministicStateStore StateStore
    {
        get => field;
        init => field = value;
    } = new InMemoryDeterministicStateStore();

    /// <summary>Logical change identifier recorded in VersionGate.</summary>
    public string ChangeId
    {
        get => field;
        init => field = value;
    } = "resourcelease.replication";

    /// <summary>Minimum supported change version.</summary>
    public int MinVersion
    {
        get => field;
        init => field = value;
    } = 1;

    /// <summary>Maximum supported change version.</summary>
    public int MaxVersion
    {
        get => field;
        init => field = value;
    } = 1;

    /// <summary>Optional factory that customizes the deterministic effect identifier per event.</summary>
    public Func<ResourceLeaseReplicationEvent, string>? EffectIdFactory
    {
        get => field;
        init => field = value;
    }

    /// <summary>Serializer context used to persist deterministic effects (defaults to <see cref="ResourceLeaseJson"/> context).</summary>
    public JsonSerializerContext? SerializerContext
    {
        get => field;
        init => field = value;
    } = ResourceLeaseJson.Context;

    /// <summary>Overrides serializer options used for deterministic persistence.</summary>
    public JsonSerializerOptions? SerializerOptions
    {
        get => field;
        init => field = value;
    }
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
        _effectStore = serializerOptions is null
            ? new DeterministicEffectStore(stateStore)
            : new DeterministicEffectStore(stateStore, serializerOptions: serializerOptions);

        var versionGate = serializerOptions is null
            ? new VersionGate(stateStore)
            : new VersionGate(stateStore, serializerOptions: serializerOptions);

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

    private static JsonSerializerOptions? CreateSerializerOptions(ResourceLeaseDeterministicOptions options)
    {
        var source = options.SerializerOptions ?? options.SerializerContext?.Options;
        if (source is null)
        {
            return null;
        }

        var serializerOptions = new JsonSerializerOptions(source);
        serializerOptions.Converters.Add(DeterministicInternalConverter.Instance);
        return serializerOptions;
    }

    private sealed class DeterministicInternalConverter : JsonConverter<object>
    {
        public static DeterministicInternalConverter Instance { get; } = new();

        private static readonly Type? VersionMarkerType = Type.GetType("Hugo.VersionGate+VersionMarker, Hugo");
        private static readonly Type? EffectEnvelopeType = Type.GetType("Hugo.DeterministicEffectStore+EffectEnvelope, Hugo");

        private static readonly ConstructorInfo? VersionMarkerCtor = VersionMarkerType?.GetConstructors(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();
        private static readonly PropertyInfo? VersionMarkerProperty = VersionMarkerType?.GetProperty("Version");

        private static readonly ConstructorInfo? EffectEnvelopeCtor = EffectEnvelopeType?.GetConstructors(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();
        private static readonly PropertyInfo? EffectEnvelopeIsSuccess = EffectEnvelopeType?.GetProperty("IsSuccess");
        private static readonly PropertyInfo? EffectEnvelopeTypeName = EffectEnvelopeType?.GetProperty("TypeName");
        private static readonly PropertyInfo? EffectEnvelopePayload = EffectEnvelopeType?.GetProperty("SerializedValue");
        private static readonly PropertyInfo? EffectEnvelopeError = EffectEnvelopeType?.GetProperty("Error");
        private static readonly PropertyInfo? EffectEnvelopeRecordedAt = EffectEnvelopeType?.GetProperty("RecordedAt");

        private DeterministicInternalConverter()
        {
        }

        public override bool CanConvert(Type typeToConvert)
        {
            if (VersionMarkerType is not null && typeToConvert == VersionMarkerType)
            {
                return true;
            }

            if (EffectEnvelopeType is not null && typeToConvert == EffectEnvelopeType)
            {
                return true;
            }

            return false;
        }

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (VersionMarkerType is not null && typeToConvert == VersionMarkerType)
            {
                return ReadVersionMarker(ref reader);
            }

            if (EffectEnvelopeType is not null && typeToConvert == EffectEnvelopeType)
            {
                return ReadEffectEnvelope(ref reader, options);
            }

            throw new JsonException($"Unsupported deterministic type '{typeToConvert}'.");
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var valueType = value.GetType();
            if (VersionMarkerType is not null && valueType == VersionMarkerType)
            {
                WriteVersionMarker(writer, value);
                return;
            }

            if (EffectEnvelopeType is not null && valueType == EffectEnvelopeType)
            {
                WriteEffectEnvelope(writer, value, options);
                return;
            }

            throw new JsonException($"Unsupported deterministic type '{valueType}'.");
        }

        private static object ReadVersionMarker(ref Utf8JsonReader reader)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var version = document.RootElement.GetProperty("Version").GetInt32();
            if (VersionMarkerCtor is null)
            {
                throw new InvalidOperationException("Unable to construct deterministic version marker.");
            }

            return VersionMarkerCtor.Invoke(new object?[] { version })!;
        }

        private static object ReadEffectEnvelope(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            var isSuccess = root.GetProperty("IsSuccess").GetBoolean();
            var typeName = root.GetProperty("TypeName").GetString() ?? string.Empty;
            byte[]? serializedValue = root.GetProperty("SerializedValue").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("SerializedValue").GetBytesFromBase64();

            Error? error = null;
            if (root.TryGetProperty("Error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
            {
                error = errorElement.Deserialize<Error>(options);
            }

            var recordedAt = root.GetProperty("RecordedAt").GetDateTimeOffset();

            if (EffectEnvelopeCtor is null)
            {
                throw new InvalidOperationException("Unable to construct deterministic effect envelope.");
            }

            return EffectEnvelopeCtor.Invoke(new object?[] { isSuccess, typeName, serializedValue, error, recordedAt })!;
        }

        private static void WriteVersionMarker(Utf8JsonWriter writer, object value)
        {
            if (VersionMarkerProperty is null)
            {
                throw new InvalidOperationException("Unable to access deterministic version marker.");
            }

            var version = (int)(VersionMarkerProperty.GetValue(value) ?? 0);
            writer.WriteStartObject();
            writer.WriteNumber("Version", version);
            writer.WriteEndObject();
        }

        private static void WriteEffectEnvelope(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (EffectEnvelopeIsSuccess is null ||
                EffectEnvelopeTypeName is null ||
                EffectEnvelopePayload is null ||
                EffectEnvelopeError is null ||
                EffectEnvelopeRecordedAt is null)
            {
                throw new InvalidOperationException("Unable to access deterministic effect envelope.");
            }

            var isSuccess = (bool)(EffectEnvelopeIsSuccess.GetValue(value) ?? false);
            var typeName = (string?)EffectEnvelopeTypeName.GetValue(value) ?? string.Empty;
            var payload = (byte[]?)EffectEnvelopePayload.GetValue(value);
            var error = (Error?)EffectEnvelopeError.GetValue(value);
            var recordedAt = (DateTimeOffset)(EffectEnvelopeRecordedAt.GetValue(value) ?? DateTimeOffset.MinValue);

            writer.WriteStartObject();
            writer.WriteBoolean("IsSuccess", isSuccess);
            writer.WriteString("TypeName", typeName);
            writer.WritePropertyName("SerializedValue");
            if (payload is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteBase64StringValue(payload);
            }

            writer.WritePropertyName("Error");
            if (error is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, error, options);
            }

            writer.WriteString("RecordedAt", recordedAt);
            writer.WriteEndObject();
        }
    }
}
