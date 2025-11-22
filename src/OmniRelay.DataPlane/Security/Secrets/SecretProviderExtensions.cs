namespace OmniRelay.Security.Secrets;

/// <summary>Helper extensions for interacting with <see cref="ISecretProvider"/> in synchronous flows.</summary>
public static class SecretProviderExtensions
{
    /// <summary>Synchronously obtains the secret when async flows are not available.</summary>
    public static SecretValue? GetSecretSync(this ISecretProvider provider, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var pending = provider.GetSecretAsync(name, cancellationToken);
        if (pending.IsCompletedSuccessfully)
        {
            return pending.Result;
        }

        return pending.AsTask().GetAwaiter().GetResult();
    }
}
