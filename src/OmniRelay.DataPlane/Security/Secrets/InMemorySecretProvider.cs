using System.Collections.Concurrent;
using Hugo;
using Microsoft.Extensions.Primitives;

namespace OmniRelay.Security.Secrets;

/// <summary>Simple in-memory provider used for tests and overrides.</summary>
public sealed class InMemorySecretProvider : ISecretProvider
{
    private readonly ConcurrentDictionary<string, byte[]> _secrets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISecretAccessAuditor _auditor;

    public static Result<InMemorySecretProvider> TryCreate(ISecretAccessAuditor auditor)
    {
        if (auditor is null)
        {
            return Result.Fail<InMemorySecretProvider>(
                Error.From("Auditor is required for InMemorySecretProvider.", "secrets.inline.auditor_missing"));
        }

        return Result.Ok(new InMemorySecretProvider(auditor));
    }

    public InMemorySecretProvider(ISecretAccessAuditor auditor)
    {
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
    }

    public void SetSecret(string name, ReadOnlySpan<byte> value)
    {
        var buffer = value.ToArray();
        _secrets.AddOrUpdate(name, _ => buffer, (_, _) => buffer);
        if (_watchers.TryRemove(name, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public ValueTask<SecretValue?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_secrets.TryGetValue(name, out var buffer))
        {
            _auditor.RecordAccess("inline", name, SecretAccessOutcome.NotFound);
            return ValueTask.FromResult<SecretValue?>(null);
        }

        var copy = buffer.ToArray();
        var metadata = new SecretMetadata(name, "inline", DateTimeOffset.UtcNow, FromCache: true, Version: null);
        var secret = new SecretValue(metadata, copy, Watch(name));
        _auditor.RecordAccess("inline", name, SecretAccessOutcome.Success);
        return ValueTask.FromResult<SecretValue?>(secret);
    }

    public IChangeToken? Watch(string name)
    {
        var cts = _watchers.GetOrAdd(name, _ => new CancellationTokenSource());
        return new CancellationChangeToken(cts.Token);
    }
}
