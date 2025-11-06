using System.IO.Compression;
using Grpc.Net.Compression;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Compression options for gRPC transport including providers and defaults.
/// </summary>
public sealed record GrpcCompressionOptions
{
    public IReadOnlyList<ICompressionProvider> Providers { get; init; } = [];

    public string? DefaultAlgorithm { get; init; }

    public CompressionLevel? DefaultCompressionLevel { get; init; }

    /// <summary>
    /// Validates that the configured default algorithm has a corresponding registered provider.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultAlgorithm))
        {
            return;
        }

        if (Providers is null)
        {
            throw new InvalidOperationException(
                $"Compression options specify default algorithm '{DefaultAlgorithm}' but no providers are registered.");
        }

        foreach (var provider in Providers)
        {
            if (provider is null)
            {
                continue;
            }

            if (string.Equals(provider.EncodingName, DefaultAlgorithm, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Compression provider for algorithm '{DefaultAlgorithm}' was not registered.");
    }
}
