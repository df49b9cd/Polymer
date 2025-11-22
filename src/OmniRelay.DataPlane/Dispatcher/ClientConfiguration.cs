using System.Collections.Immutable;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Exposes outbound bindings and middleware for a specific remote service.
/// </summary>
public sealed class ClientConfiguration
{
    private readonly OutboundRegistry _outbounds;
    private readonly ImmutableArray<IUnaryOutboundMiddleware> _unaryMiddleware;
    private readonly ImmutableArray<IOnewayOutboundMiddleware> _onewayMiddleware;
    private readonly ImmutableArray<IStreamOutboundMiddleware> _streamMiddleware;
    private readonly ImmutableArray<IClientStreamOutboundMiddleware> _clientStreamMiddleware;
    private readonly ImmutableArray<IDuplexOutboundMiddleware> _duplexMiddleware;

    internal ClientConfiguration(
        OutboundRegistry outbounds,
        ImmutableArray<IUnaryOutboundMiddleware> unaryMiddleware,
        ImmutableArray<IOnewayOutboundMiddleware> onewayMiddleware,
        ImmutableArray<IStreamOutboundMiddleware> streamMiddleware,
        ImmutableArray<IClientStreamOutboundMiddleware> clientStreamMiddleware,
        ImmutableArray<IDuplexOutboundMiddleware> duplexMiddleware)
    {
        _outbounds = outbounds ?? throw new ArgumentNullException(nameof(outbounds));
        _unaryMiddleware = unaryMiddleware;
        _onewayMiddleware = onewayMiddleware;
        _streamMiddleware = streamMiddleware;
        _clientStreamMiddleware = clientStreamMiddleware;
        _duplexMiddleware = duplexMiddleware;
    }

    /// <summary>Gets the remote service name.</summary>
    public string Service => _outbounds.Service;

    public IReadOnlyDictionary<string, IUnaryOutbound> Unary => _outbounds.Unary;
    public IReadOnlyDictionary<string, IOnewayOutbound> Oneway => _outbounds.Oneway;
    public IReadOnlyDictionary<string, IStreamOutbound> Stream => _outbounds.Stream;
    public IReadOnlyDictionary<string, IClientStreamOutbound> ClientStream => _outbounds.ClientStream;
    public IReadOnlyDictionary<string, IDuplexOutbound> Duplex => _outbounds.Duplex;

    /// <summary>Gets unary outbound middleware.</summary>
    public IReadOnlyList<IUnaryOutboundMiddleware> UnaryMiddleware => _unaryMiddleware;
    public IReadOnlyList<IOnewayOutboundMiddleware> OnewayMiddleware => _onewayMiddleware;
    public IReadOnlyList<IStreamOutboundMiddleware> StreamMiddleware => _streamMiddleware;
    public IReadOnlyList<IClientStreamOutboundMiddleware> ClientStreamMiddleware => _clientStreamMiddleware;
    public IReadOnlyList<IDuplexOutboundMiddleware> DuplexMiddleware => _duplexMiddleware;

    /// <summary>Resolves a unary outbound by key, or default when not specified.</summary>
    public IUnaryOutbound? ResolveUnary(string? key = null) => _outbounds.ResolveUnary(key);

    public IOnewayOutbound? ResolveOneway(string? key = null) => _outbounds.ResolveOneway(key);

    public IStreamOutbound? ResolveStream(string? key = null) => _outbounds.ResolveStream(key);

    public IClientStreamOutbound? ResolveClientStream(string? key = null) => _outbounds.ResolveClientStream(key);

    public IDuplexOutbound? ResolveDuplex(string? key = null) => _outbounds.ResolveDuplex(key);

    public bool TryGetUnary(string? key, out IUnaryOutbound? outbound) =>
        _outbounds.TryGetUnary(key, out outbound);

    public bool TryGetOneway(string? key, out IOnewayOutbound? outbound) =>
        _outbounds.TryGetOneway(key, out outbound);

    public bool TryGetStream(string? key, out IStreamOutbound? outbound) =>
        _outbounds.TryGetStream(key, out outbound);

    public bool TryGetClientStream(string? key, out IClientStreamOutbound? outbound) =>
        _outbounds.TryGetClientStream(key, out outbound);

    public bool TryGetDuplex(string? key, out IDuplexOutbound? outbound) =>
        _outbounds.TryGetDuplex(key, out outbound);
}
