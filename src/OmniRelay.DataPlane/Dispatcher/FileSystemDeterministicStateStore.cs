using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hugo;
using static Hugo.Go;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Stores deterministic records as JSON documents on the filesystem.
/// </summary>
public sealed partial class FileSystemDeterministicStateStore : IDeterministicStateStore, IDisposable
{
    private const int MaxStackUtf8Bytes = 1024;
    private readonly string _root;
    private readonly ReaderWriterLockSlim _lock = new();
    private static readonly FileSystemDeterministicStateStoreJsonContext JsonContext = FileSystemDeterministicStateStoreJsonContext.Default;

    private FileSystemDeterministicStateStore(string rootDirectory)
    {
        _root = rootDirectory;
    }

    public static Result<FileSystemDeterministicStateStore> Create(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return Err<FileSystemDeterministicStateStore>(ResourceLeaseDeterministicErrors.RootDirectoryRequired());
        }

        try
        {
            var fullPath = Path.GetFullPath(rootDirectory);
            Directory.CreateDirectory(fullPath);
            return Ok(new FileSystemDeterministicStateStore(fullPath));
        }
        catch (Exception ex)
        {
            return Err<FileSystemDeterministicStateStore>(Error.FromException(ex)
                .WithMetadata("rootDirectory", rootDirectory));
        }
    }

    public bool TryGet(string key, out DeterministicRecord record)
    {
        var path = GetPath(key);
        _lock.EnterReadLock();
        FileStream? stream = null;
        try
        {
            if (!File.Exists(path))
            {
                record = null!;
                return false;
            }

            stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    Options = FileOptions.SequentialScan
                });

            var model = JsonSerializer.Deserialize(stream, JsonContext.RecordModel)!;
            record = model.ToRecord();
            return true;
        }
        finally
        {
            stream?.Dispose();
            _lock.ExitReadLock();
        }
    }

    public void Set(string key, DeterministicRecord record)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(record);

        var path = GetPath(key);
        var model = RecordModel.FromRecord(record);
        _lock.EnterWriteLock();
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.Create,
                    Share = FileShare.None,
                    Options = FileOptions.None
                });

            JsonSerializer.Serialize(stream, model, JsonContext.RecordModel);
        }
        finally
        {
            stream?.Dispose();
            _lock.ExitWriteLock();
        }
    }

    public bool TryAdd(string key, DeterministicRecord record)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(record);

        var path = GetPath(key);
        var model = RecordModel.FromRecord(record);

        _lock.EnterWriteLock();
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    Options = FileOptions.None
                });

            JsonSerializer.Serialize(stream, model, JsonContext.RecordModel);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        finally
        {
            stream?.Dispose();
            _lock.ExitWriteLock();
        }
    }

    private string GetPath(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var hash = HashKey(key);
        var directory = Path.Combine(_root, hash[..2]);
        return Path.Combine(directory, $"{hash}.json");
    }

    public void Dispose()
    {
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record RecordModel(string Kind, int Version, DateTimeOffset RecordedAt, byte[] Payload)
    {
        public DeterministicRecord ToRecord() => new(Kind, Version, Payload, RecordedAt);

        public static RecordModel FromRecord(DeterministicRecord record)
        {
            var payload = record.Payload;
            if (MemoryMarshal.TryGetArray(payload, out var segment) &&
                segment.Array is { } array &&
                segment.Offset == 0 &&
                segment.Count == array.Length)
            {
                return new RecordModel(record.Kind, record.Version, record.RecordedAt, array);
            }

            return new RecordModel(record.Kind, record.Version, record.RecordedAt, payload.ToArray());
        }
    }

    private static string HashKey(string key)
    {
        // Avoid heap allocations for common key sizes by using stackalloc where possible,
        // and fall back to a pooled buffer for very large keys.
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(key.Length);

        byte[]? rented = null;
        Span<byte> utf8 = maxByteCount <= MaxStackUtf8Bytes
            ? stackalloc byte[MaxStackUtf8Bytes]
            : rented = ArrayPool<byte>.Shared.Rent(maxByteCount);

        try
        {
            var written = Encoding.UTF8.GetBytes(key.AsSpan(), utf8);
            Span<byte> hash = stackalloc byte[32];
            _ = SHA256.HashData(utf8[..written], hash);
            return Convert.ToHexString(hash);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(RecordModel))]
    private sealed partial class FileSystemDeterministicStateStoreJsonContext : JsonSerializerContext;
}
