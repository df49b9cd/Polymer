using System.Text;
using Microsoft.Extensions.Primitives;

namespace OmniRelay.Security.Secrets;

/// <summary>Reads secrets from process environment variables.</summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    private readonly string? _prefix;
    private readonly ISecretAccessAuditor _auditor;

    public EnvironmentSecretProvider(ISecretAccessAuditor auditor, string? prefix = null)
    {
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _prefix = prefix;
    }

    public ValueTask<SecretValue?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Secret name cannot be null or whitespace.", nameof(name));
        }

        var candidate = BuildVariableName(name);
        var value = Environment.GetEnvironmentVariable(candidate) ??
                    Environment.GetEnvironmentVariable(candidate.ToUpperInvariant());

        if (string.IsNullOrEmpty(value))
        {
            _auditor.RecordAccess("environment", name, SecretAccessOutcome.NotFound);
            return ValueTask.FromResult<SecretValue?>(null);
        }

        var metadata = new SecretMetadata(name, "environment", DateTimeOffset.UtcNow, FromCache: false, Version: null);
        var bytes = Encoding.UTF8.GetBytes(value);
        var secret = new SecretValue(metadata, bytes);
        _auditor.RecordAccess("environment", name, SecretAccessOutcome.Success);
        return ValueTask.FromResult<SecretValue?>(secret);
    }

    public IChangeToken? Watch(string name) => null;

    private string BuildVariableName(string name) =>
        string.IsNullOrEmpty(_prefix) ? name : $"{_prefix}{name}";
}
