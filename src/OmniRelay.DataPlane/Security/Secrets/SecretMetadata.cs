namespace OmniRelay.Security.Secrets;

/// <summary>Metadata describing the retrieval of a secret.</summary>
public readonly record struct SecretMetadata(
    string Name,
    string ProviderName,
    DateTimeOffset RetrievedAt,
    bool FromCache,
    string? Version);
