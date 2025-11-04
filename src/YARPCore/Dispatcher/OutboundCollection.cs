using System.Collections.Immutable;
using YARPCore.Core.Transport;

namespace YARPCore.Dispatcher;

public sealed class OutboundCollection
{
    public const string DefaultKey = "default";

    private readonly ImmutableDictionary<string, IUnaryOutbound> _unary;
    private readonly ImmutableDictionary<string, IOnewayOutbound> _oneway;
    private readonly ImmutableDictionary<string, IStreamOutbound> _stream;
    private readonly ImmutableDictionary<string, IClientStreamOutbound> _clientStream;
    private readonly ImmutableDictionary<string, IDuplexOutbound> _duplex;

    internal OutboundCollection(
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

    public string Service { get; }

    public IReadOnlyDictionary<string, IUnaryOutbound> Unary => _unary;
    public IReadOnlyDictionary<string, IOnewayOutbound> Oneway => _oneway;
    public IReadOnlyDictionary<string, IStreamOutbound> Stream => _stream;
    public IReadOnlyDictionary<string, IClientStreamOutbound> ClientStream => _clientStream;
    public IReadOnlyDictionary<string, IDuplexOutbound> Duplex => _duplex;

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

    private static string NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? DefaultKey : key!;

}
