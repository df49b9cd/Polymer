using System.Threading.Channels;

namespace OmniRelay.Core.Transport;

/// <summary>
/// Provides access to request metadata and the raw request message reader for client-streaming handlers.
/// </summary>
public readonly struct ClientStreamRequestContext(RequestMeta meta, ChannelReader<ReadOnlyMemory<byte>> requests) : IEquatable<ClientStreamRequestContext>
{
    /// <summary>Gets the request metadata.</summary>
    public RequestMeta Meta { get; } = meta;

    /// <summary>Gets the raw request message reader.</summary>
    public ChannelReader<ReadOnlyMemory<byte>> Requests { get; } = requests;

    public override bool Equals(object obj)
    {
        throw new NotImplementedException();
    }

    public override int GetHashCode()
    {
        throw new NotImplementedException();
    }

    public static bool operator ==(ClientStreamRequestContext left, ClientStreamRequestContext right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ClientStreamRequestContext left, ClientStreamRequestContext right)
    {
        return !(left == right);
    }

    public bool Equals(ClientStreamRequestContext other)
    {
        throw new NotImplementedException();
    }
}
