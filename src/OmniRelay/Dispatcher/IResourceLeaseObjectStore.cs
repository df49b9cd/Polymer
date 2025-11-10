namespace OmniRelay.Dispatcher;

/// <summary>
/// Minimal abstraction for writing replication artifacts to an object storage system (for example S3, GCS, or Azure Blob Storage).
/// </summary>
public interface IResourceLeaseObjectStore
{
    /// <summary>Lists object keys that share the provided prefix.</summary>
    ValueTask<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Writes a new object at the specified key overwriting any prior content.</summary>
    ValueTask WriteAsync(string key, ReadOnlyMemory<byte> payload, string contentType, CancellationToken cancellationToken);
}
