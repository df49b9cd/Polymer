using Microsoft.Extensions.Primitives;

namespace OmniRelay.Security.Secrets;

/// <summary>Queries multiple providers in precedence order.</summary>
public sealed class CompositeSecretProvider : ISecretProvider, IDisposable
{
    private readonly ISecretProvider[] _providers;
    private readonly ISecretAccessAuditor _auditor;
    private bool _disposed;

    public CompositeSecretProvider(
        IEnumerable<ISecretProvider> providers,
        ISecretAccessAuditor auditor)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(auditor);

        _providers = providers.ToArray();
        if (_providers.Length == 0)
        {
            throw new ArgumentException("At least one secret provider must be supplied.", nameof(providers));
        }

        _auditor = auditor;
    }

    public async ValueTask<SecretValue?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Secret name cannot be null or whitespace.", nameof(name));
        }

        foreach (var provider in _providers)
        {
            SecretValue? value = null;
            try
            {
                value = await provider.GetSecretAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _auditor.RecordAccess(provider.GetType().Name, name, SecretAccessOutcome.Failed, ex);
                throw;
            }

            if (value is null)
            {
                _auditor.RecordAccess(provider.GetType().Name, name, SecretAccessOutcome.NotFound);
                continue;
            }

            _auditor.RecordAccess(provider.GetType().Name, name, SecretAccessOutcome.Success);
            return value;
        }

        return null;
    }

    public IChangeToken? Watch(string name)
    {
        var tokens = new List<IChangeToken>(_providers.Length);
        foreach (var provider in _providers)
        {
            var token = provider.Watch(name);
            if (token is not null)
            {
                tokens.Add(token);
            }
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        return tokens.Count == 1 ? tokens[0] : new CompositeChangeToken(tokens);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var disposable in _providers.OfType<IDisposable>())
        {
            disposable.Dispose();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
