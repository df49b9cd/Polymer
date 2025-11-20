namespace OmniRelay.Cli.Core;

/// <summary>
/// Central registry for protobuf payload encoders. Generated modules can register
/// strongly-typed serializers so the CLI avoids reflection and stays AOT-safe.
/// </summary>
public sealed class ProtobufPayloadRegistry
{
    public static ProtobufPayloadRegistry Instance { get; } = new();

    private readonly Dictionary<string, Func<string, ReadOnlyMemory<byte>>> encoders = new(StringComparer.Ordinal);
    private readonly object gate = new();

    private ProtobufPayloadRegistry()
    {
    }

    /// <summary>
    /// Register a JSON-to-binary encoder for a fully-qualified protobuf message name.
    /// </summary>
    public void Register(string fullName, Func<string, ReadOnlyMemory<byte>> encoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentNullException.ThrowIfNull(encoder);

        lock (gate)
        {
            encoders[Normalize(fullName)] = encoder;
        }
    }

    public bool TryEncode(string fullName, string json, out ReadOnlyMemory<byte> payload, out string? error)
    {
        payload = ReadOnlyMemory<byte>.Empty;
        error = null;

        var key = Normalize(fullName);
        Func<string, ReadOnlyMemory<byte>>? encoder;
        lock (gate)
        {
            encoders.TryGetValue(key, out encoder);
        }

        if (encoder is null)
        {
            error = $"No protobuf encoder registered for '{fullName}'. Provide --body-base64 or add a module that registers generated types.";
            return false;
        }

        try
        {
            payload = encoder(json ?? "{}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to encode protobuf payload for '{fullName}': {ex.Message}";
            return false;
        }
    }

    private static string Normalize(string name) => name.StartsWith('.') ? name[1..] : name;
}
