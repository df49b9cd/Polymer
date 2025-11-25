using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hugo;
using Unit = Hugo.Go.Unit;
using static Hugo.Go;
using OmniRelay.Identity;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Persists last-known-good control snapshot to disk for agent/edge resilience.</summary>
public sealed class LkgCache
{
    private const int FormatVersion = 1;
    private readonly string _path;
    private readonly LkgCacheOptions _options;

    internal sealed record LkgEnvelope(
        int FormatVersion,
        string ConfigVersion,
        long Epoch,
        byte[] Payload,
        byte[] ResumeToken,
        byte[] Hash,
        byte[]? Signature,
        string HashAlgorithm);

    public LkgCache(string path, LkgCacheOptions? options = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _options = options ?? new LkgCacheOptions();
    }

    public ValueTask<Result<Unit>> SaveAsync(string version, long epoch, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte> resumeToken, CancellationToken cancellationToken = default)
    {
        return Result.TryAsync<Unit>(async ct =>
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var hash = ComputeHash(version, epoch, payload.Span, resumeToken.Span);
            var signature = ComputeSignature(hash);

            var envelope = new LkgEnvelope(
                FormatVersion,
                version,
                epoch,
                payload.ToArray(),
                resumeToken.ToArray(),
                hash,
                signature,
                _options.HashAlgorithm);

            var stream = new FileStream(
                _path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough);

            try
            {
                await JsonSerializer.SerializeAsync(stream, envelope, LkgCacheJsonContext.Default.LkgEnvelope, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            return Unit.Value;
        }, cancellationToken: cancellationToken);
    }

    public async ValueTask<Result<LkgSnapshot?>> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Ok<LkgSnapshot?>(null);
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var envelope = await JsonSerializer.DeserializeAsync(stream, LkgCacheJsonContext.Default.LkgEnvelope, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                return Ok<LkgSnapshot?>(null);
            }

            if (envelope.FormatVersion != FormatVersion)
            {
                return Err<LkgSnapshot?>(Error.From("Unsupported LKG cache format.", LkgCacheErrorCodes.UnsupportedFormat));
            }

            if (!ValidateHash(envelope))
            {
                return Err<LkgSnapshot?>(Error.From("LKG cache hash mismatch.", LkgCacheErrorCodes.HashMismatch));
            }

            var signatureCheck = ValidateSignature(envelope);
            if (signatureCheck.IsFailure)
            {
                return signatureCheck.CastFailure<LkgSnapshot?>();
            }

            return Ok<LkgSnapshot?>(new LkgSnapshot(envelope.ConfigVersion, envelope.Epoch, envelope.Payload, envelope.ResumeToken));
        }
        catch (OperationCanceledException oce) when (cancellationToken.IsCancellationRequested)
        {
            return Err<LkgSnapshot?>(Error.Canceled("LKG load canceled", oce.CancellationToken));
        }
        catch (Exception ex)
        {
            return Err<LkgSnapshot?>(Error.FromException(ex, "lkg.cache.read_failed"));
        }
    }

    private byte[] ComputeHash(string version, long epoch, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> resumeToken)
    {
        var algorithm = ResolveHashAlgorithm();
        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(payload);
        var versionBytes = Encoding.UTF8.GetBytes(version);
        hash.AppendData(versionBytes);

        Span<byte> epochBuffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(epochBuffer, epoch);
        hash.AppendData(epochBuffer);
        hash.AppendData(resumeToken);

        return hash.GetHashAndReset();
    }

    private bool ValidateHash(LkgEnvelope envelope)
    {
        if (envelope.Hash is null || envelope.Hash.Length == 0)
        {
            return false;
        }

        var computed = ComputeHash(envelope.ConfigVersion, envelope.Epoch, envelope.Payload, envelope.ResumeToken);
        return CryptographicOperations.FixedTimeEquals(computed, envelope.Hash);
    }

    private byte[]? ComputeSignature(ReadOnlySpan<byte> hash)
    {
        if (_options.SigningKey is null || _options.SigningKey.Length == 0)
        {
            return null;
        }

        using var hmac = new HMACSHA256(_options.SigningKey);
        return hmac.ComputeHash(hash.ToArray());
    }

    private Result<Unit> ValidateSignature(LkgEnvelope envelope)
    {
        var signatureRequired = _options.RequireSignature || envelope.Signature is not null;
        if (!signatureRequired)
        {
            return Ok(Unit.Value);
        }

        if (_options.SigningKey is null || _options.SigningKey.Length == 0)
        {
            return Err<Unit>(Error.From("LKG cache signature required but no signing key configured.", LkgCacheErrorCodes.SignatureMissing));
        }

        if (envelope.Signature is null || envelope.Signature.Length == 0)
        {
            return Err<Unit>(Error.From("LKG cache signature missing.", LkgCacheErrorCodes.SignatureMissing));
        }

        var computed = ComputeSignature(envelope.Hash);
        if (computed is null || !CryptographicOperations.FixedTimeEquals(computed, envelope.Signature))
        {
            return Err<Unit>(Error.From("LKG cache signature invalid.", LkgCacheErrorCodes.SignatureInvalid));
        }

        return Ok(Unit.Value);
    }

    private HashAlgorithmName ResolveHashAlgorithm() =>
        _options.HashAlgorithm?.ToUpperInvariant() switch
        {
            "SHA512" => HashAlgorithmName.SHA512,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA1" => HashAlgorithmName.SHA1,
            _ => HashAlgorithmName.SHA256
        };
}

public sealed record LkgSnapshot(string Version, long Epoch, byte[] Payload, byte[] ResumeToken);

internal static class LkgCacheErrorCodes
{
    public const string UnsupportedFormat = "lkg.cache.format.unsupported";
    public const string HashMismatch = "lkg.cache.hash_mismatch";
    public const string SignatureMissing = "lkg.cache.signature_missing";
    public const string SignatureInvalid = "lkg.cache.signature_invalid";
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LkgCache.LkgEnvelope))]
internal partial class LkgCacheJsonContext : JsonSerializerContext
{
}
