using System.IO.Hashing;
using System.Text;

namespace OmniRelay.Core.Shards.Hashing;

internal static class ShardHashingPrimitives
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public static ulong Hash(ReadOnlySpan<char> value)
    {
        Span<byte> buffer = stackalloc byte[Utf8.GetByteCount(value)];
        var written = Utf8.GetBytes(value, buffer);
        return XxHash64.HashToUInt64(buffer[..written]);
    }

    public static ulong Hash(string value)
    {
        var bytes = Utf8.GetBytes(value);
        return XxHash64.HashToUInt64(bytes);
    }

    public static double Normalize(ulong hash) => hash / (double)ulong.MaxValue;
}
