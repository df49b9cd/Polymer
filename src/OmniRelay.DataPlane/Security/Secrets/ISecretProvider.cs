using Microsoft.Extensions.Primitives;

namespace OmniRelay.Security.Secrets;

/// <summary>
/// Resolves named secrets from secure stores (environment, files, vaults) with optional change notifications.
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Retrieves the secret identified by <paramref name="name"/>.
    /// Implementations may cache or stream secret material; callers must dispose the returned value to zero buffers.
    /// </summary>
    /// <param name="name">Logical secret name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Secret material when found; otherwise <c>null</c>.</returns>
    ValueTask<SecretValue?> GetSecretAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a change token that fires when the secret identified by <paramref name="name"/> rotates.
    /// </summary>
    /// <param name="name">Logical secret name.</param>
    /// <returns>The change token or <c>null</c> when change notifications are not supported.</returns>
    IChangeToken? Watch(string name);
}
