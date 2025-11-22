using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace OmniRelay.Security.Secrets;

/// <summary>
/// Represents secret material retrieved from an <see cref="ISecretProvider"/>.
/// Buffers are zeroed when disposed.
/// </summary>
public sealed class SecretValue : IDisposable
{
    private readonly byte[]? _buffer;
    private readonly Action<SecretValue>? _onDispose;
    private bool _disposed;

    internal SecretValue(
        SecretMetadata metadata,
        byte[]? buffer,
        IChangeToken? changeToken = null,
        Action<SecretValue>? onDispose = null)
    {
        Metadata = metadata;
        _buffer = buffer;
        ChangeToken = changeToken;
        _onDispose = onDispose;
    }

    /// <summary>Metadata for the retrieved secret.</summary>
    public SecretMetadata Metadata { get; }

    /// <summary>Change token that fires when the secret rotates.</summary>
    public IChangeToken? ChangeToken { get; }

    /// <summary>Returns secret material as a read-only memory buffer.</summary>
    public ReadOnlyMemory<byte> AsMemory() =>
        _buffer is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(_buffer);

    /// <summary>Decodes the buffer as UTF-8 text.</summary>
    public string? AsString()
    {
        if (_buffer is null || _buffer.Length == 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(_buffer);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_buffer is { Length: > 0 })
        {
            CryptographicOperations.ZeroMemory(_buffer);
        }

        _onDispose?.Invoke(this);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
