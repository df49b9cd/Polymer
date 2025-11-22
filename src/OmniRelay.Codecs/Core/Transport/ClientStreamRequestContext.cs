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

    public override bool Equals(object? obj) => obj is ClientStreamRequestContext other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Meta, Requests);

    public static bool operator ==(ClientStreamRequestContext left, ClientStreamRequestContext right) => left.Equals(right);

    public static bool operator !=(ClientStreamRequestContext left, ClientStreamRequestContext right) => !(left == right);

    public bool Equals(ClientStreamRequestContext other) =>
        EqualityComparer<RequestMeta>.Default.Equals(Meta, other.Meta) &&
        ReferenceEquals(Requests, other.Requests);
}
