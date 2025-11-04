using System.Threading.Channels;

namespace YARPCore.Core.Transport;

public readonly struct ClientStreamRequestContext(RequestMeta meta, ChannelReader<ReadOnlyMemory<byte>> requests)
{
    public RequestMeta Meta { get; } = meta;
    public ChannelReader<ReadOnlyMemory<byte>> Requests { get; } = requests;
}
