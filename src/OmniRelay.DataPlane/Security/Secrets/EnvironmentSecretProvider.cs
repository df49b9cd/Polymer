using System.Text;
using Hugo;
using Microsoft.Extensions.Primitives;

namespace OmniRelay.Security.Secrets;

/// <summary>Reads secrets from process environment variables.</summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    private readonly string? _prefix;
    private readonly ISecretAccessAuditor _auditor;

    public static Result<EnvironmentSecretProvider> TryCreate(ISecretAccessAuditor auditor, string? prefix = null)
    {
        if (auditor is null)
        {
            return Result.Fail<EnvironmentSecretProvider>(
                Error.From("Auditor is required for EnvironmentSecretProvider.", "secrets.env.auditor_missing"));
        }

        return Result.Ok(new EnvironmentSecretProvider(auditor, prefix));
    }

    public EnvironmentSecretProvider(ISecretAccessAuditor auditor, string? prefix = null)
    {
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _prefix = prefix;
    }

    public ValueTask<SecretValue?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValueTask.FromResult<SecretValue?>(
                null);
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
