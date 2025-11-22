using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Persists last-known-good control snapshot to disk for agent/edge resilience.</summary>
public sealed class LkgCache
{
    private readonly string _path;

    internal sealed record LkgEnvelope(string Version, byte[] Payload);

    public LkgCache(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public void Save(string version, byte[] payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var envelope = new LkgEnvelope(version, payload);
        var json = JsonSerializer.Serialize(envelope, LkgCacheJsonContext.Default.LkgEnvelope);
        File.WriteAllText(_path, json);
    }

    public bool TryLoad(out string version, out byte[] payload)
    {
        version = "";
        payload = Array.Empty<byte>();
        if (!File.Exists(_path))
        {
            return false;
        }

        var json = File.ReadAllText(_path);
        var envelope = JsonSerializer.Deserialize(json, LkgCacheJsonContext.Default.LkgEnvelope);
        if (envelope is null)
        {
            return false;
        }

        version = envelope.Version;
        payload = envelope.Payload;
        return true;
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LkgCache.LkgEnvelope))]
internal partial class LkgCacheJsonContext : JsonSerializerContext
{
}
