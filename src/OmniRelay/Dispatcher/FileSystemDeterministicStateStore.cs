using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hugo;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Stores deterministic records as JSON documents on the filesystem.
/// </summary>
public sealed partial class FileSystemDeterministicStateStore : IDeterministicStateStore
{
    private readonly string _root;
    private readonly ReaderWriterLockSlim _lock = new();
    private static readonly FileSystemDeterministicStateStoreJsonContext JsonContext = FileSystemDeterministicStateStoreJsonContext.Default;

    public FileSystemDeterministicStateStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
        }

        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public bool TryGet(string key, out DeterministicRecord record)
    {
        var path = GetPath(key);
        _lock.EnterReadLock();
        try
        {
            if (!File.Exists(path))
            {
                record = null!;
                return false;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var model = JsonSerializer.Deserialize(json, JsonContext.RecordModel)!;
            record = model.ToRecord();
            return true;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Set(string key, DeterministicRecord record)
    {
        var path = GetPath(key);
        var model = RecordModel.FromRecord(record);
        var json = JsonSerializer.Serialize(model, JsonContext.RecordModel);

        _lock.EnterWriteLock();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool TryAdd(string key, DeterministicRecord record)
    {
        var path = GetPath(key);
        var model = RecordModel.FromRecord(record);
        var json = JsonSerializer.Serialize(model, JsonContext.RecordModel);

        _lock.EnterWriteLock();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(json);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var directory = Path.Combine(_root, hash[..2]);
        return Path.Combine(directory, $"{hash}.json");
    }

    private sealed record RecordModel(string Kind, int Version, DateTimeOffset RecordedAt, byte[] Payload)
    {
        public DeterministicRecord ToRecord() => new(Kind, Version, Payload, RecordedAt);

        public static RecordModel FromRecord(DeterministicRecord record) =>
            new(record.Kind, record.Version, record.RecordedAt, record.Payload.ToArray());
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(RecordModel))]
    private sealed partial class FileSystemDeterministicStateStoreJsonContext : JsonSerializerContext;
}
