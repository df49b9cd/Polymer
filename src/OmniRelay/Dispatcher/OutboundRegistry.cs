using System.Collections.Immutable;
using OmniRelay.Core.Transport;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Resolves outbound transports by key for a remote service across all RPC shapes.
/// </summary>
public sealed class OutboundRegistry
{
    public const string DefaultKey = "default";

    private readonly ImmutableDictionary<string, IUnaryOutbound> _unary;
    private readonly ImmutableDictionary<string, IOnewayOutbound> _oneway;
    private readonly ImmutableDictionary<string, IStreamOutbound> _stream;
    private readonly ImmutableDictionary<string, IClientStreamOutbound> _clientStream;
    private readonly ImmutableDictionary<string, IDuplexOutbound> _duplex;

    internal OutboundRegistry(
        string service,
        ImmutableDictionary<string, IUnaryOutbound> unary,
        ImmutableDictionary<string, IOnewayOutbound> oneway,
        ImmutableDictionary<string, IStreamOutbound> stream,
        ImmutableDictionary<string, IClientStreamOutbound> clientStream,
        ImmutableDictionary<string, IDuplexOutbound> duplex)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        ArgumentNullException.ThrowIfNull(unary);
        ArgumentNullException.ThrowIfNull(oneway);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(clientStream);
        ArgumentNullException.ThrowIfNull(duplex);
        _unary = unary!;
        _oneway = oneway!;
        _stream = stream!;
        _clientStream = clientStream!;
        _duplex = duplex!;
    }

    /// <summary>Gets the remote service name for this collection.</summary>
    public string Service { get; }

    /// <summary>Gets the unary outbound bindings by key.</summary>
    public IReadOnlyDictionary<string, IUnaryOutbound> Unary => _unary;
    public IReadOnlyDictionary<string, IOnewayOutbound> Oneway => _oneway;
    public IReadOnlyDictionary<string, IStreamOutbound> Stream => _stream;
    public IReadOnlyDictionary<string, IClientStreamOutbound> ClientStream => _clientStream;
    public IReadOnlyDictionary<string, IDuplexOutbound> Duplex => _duplex;

    /// <summary>Tries to resolve a unary outbound by key.</summary>
    public bool TryGetUnary(string? key, out IUnaryOutbound? outbound) =>
        _unary.TryGetValue(NormalizeKey(key), out outbound);

    public bool TryGetOneway(string? key, out IOnewayOutbound? outbound) =>
        _oneway.TryGetValue(NormalizeKey(key), out outbound);

    public bool TryGetStream(string? key, out IStreamOutbound? outbound) =>
        _stream.TryGetValue(NormalizeKey(key), out outbound);

    public bool TryGetClientStream(string? key, out IClientStreamOutbound? outbound) =>
        _clientStream.TryGetValue(NormalizeKey(key), out outbound);

    public bool TryGetDuplex(string? key, out IDuplexOutbound? outbound) =>
        _duplex.TryGetValue(NormalizeKey(key), out outbound);

    /// <summary>Resolves a unary outbound by key, returning <c>null</c> if not found.</summary>
    public IUnaryOutbound? ResolveUnary(string? key = null) =>
        _unary.TryGetValue(NormalizeKey(key), out var outbound) ? outbound : null;

    public IOnewayOutbound? ResolveOneway(string? key = null) =>
        _oneway.TryGetValue(NormalizeKey(key), out var outbound) ? outbound : null;

    public IStreamOutbound? ResolveStream(string? key = null) =>
        _stream.TryGetValue(NormalizeKey(key), out var outbound) ? outbound : null;

    public IClientStreamOutbound? ResolveClientStream(string? key = null) =>
        _clientStream.TryGetValue(NormalizeKey(key), out var outbound) ? outbound : null;

    public IDuplexOutbound? ResolveDuplex(string? key = null) =>
        _duplex.TryGetValue(NormalizeKey(key), out var outbound) ? outbound : null;

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return DefaultKey;
        }

        return key!.Trim();
    }

}
