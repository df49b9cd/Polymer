using Grpc.Core;

namespace OmniRelay.Transport.Grpc;

internal static class GrpcMetadataExtensions
{
    public static string? GetValue(this Metadata metadata, string key)
    {
        if (metadata is null)
        {
            return null;
        }

        for (var i = 0; i < metadata.Count; i++)
        {
            var entry = metadata[i];
            if (entry.IsBinary)
            {
                continue;
            }

            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }
}
