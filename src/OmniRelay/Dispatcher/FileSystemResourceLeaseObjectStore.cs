using System.IO;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Simple object store implementation that persists blobs to the local filesystem. Useful for tests and single-node deployments.
/// </summary>
public sealed class FileSystemResourceLeaseObjectStore : IResourceLeaseObjectStore
{
    private readonly string _root;

    public FileSystemResourceLeaseObjectStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
        }

        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public ValueTask<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(_root))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var normalizedPrefix = (prefix ?? string.Empty).Replace('\\', '/');
        var files = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_root, file)
                .Replace(Path.DirectorySeparatorChar, '/');

            if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(normalizedPrefix) &&
                !relative.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            files.Add(relative);
        }

        files.Sort(StringComparer.Ordinal);
        return ValueTask.FromResult<IReadOnlyList<string>>(files);
    }

    public async ValueTask WriteAsync(string key, ReadOnlyMemory<byte> payload, string contentType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key must be provided.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = GetPathForKey(key);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private string GetPathForKey(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Object keys cannot traverse directories.");
        }

        var normalized = key.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(_root, Path.Combine(segments));
    }
}
