using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;

namespace Polymer.Dispatcher;

public sealed class ClientConfiguration
{
    private readonly OutboundCollection _outbounds;
    private readonly ImmutableArray<IUnaryOutboundMiddleware> _unaryMiddleware;
    private readonly ImmutableArray<IOnewayOutboundMiddleware> _onewayMiddleware;
    private readonly ImmutableArray<IStreamOutboundMiddleware> _streamMiddleware;
    private readonly ImmutableArray<IClientStreamOutboundMiddleware> _clientStreamMiddleware;

    internal ClientConfiguration(
        OutboundCollection outbounds,
        ImmutableArray<IUnaryOutboundMiddleware> unaryMiddleware,
        ImmutableArray<IOnewayOutboundMiddleware> onewayMiddleware,
        ImmutableArray<IStreamOutboundMiddleware> streamMiddleware,
        ImmutableArray<IClientStreamOutboundMiddleware> clientStreamMiddleware)
    {
        _outbounds = outbounds ?? throw new ArgumentNullException(nameof(outbounds));
        _unaryMiddleware = unaryMiddleware;
        _onewayMiddleware = onewayMiddleware;
        _streamMiddleware = streamMiddleware;
        _clientStreamMiddleware = clientStreamMiddleware;
    }

    public string Service => _outbounds.Service;

    public IReadOnlyDictionary<string, IUnaryOutbound> Unary => _outbounds.Unary;
    public IReadOnlyDictionary<string, IOnewayOutbound> Oneway => _outbounds.Oneway;
    public IReadOnlyDictionary<string, IStreamOutbound> Stream => _outbounds.Stream;
    public IReadOnlyDictionary<string, IClientStreamOutbound> ClientStream => _outbounds.ClientStream;

    public IReadOnlyList<IUnaryOutboundMiddleware> UnaryMiddleware => _unaryMiddleware;
    public IReadOnlyList<IOnewayOutboundMiddleware> OnewayMiddleware => _onewayMiddleware;
    public IReadOnlyList<IStreamOutboundMiddleware> StreamMiddleware => _streamMiddleware;
    public IReadOnlyList<IClientStreamOutboundMiddleware> ClientStreamMiddleware => _clientStreamMiddleware;

    public IUnaryOutbound? ResolveUnary(string? key = null) => _outbounds.ResolveUnary(key);

    public IOnewayOutbound? ResolveOneway(string? key = null) => _outbounds.ResolveOneway(key);

    public IStreamOutbound? ResolveStream(string? key = null) => _outbounds.ResolveStream(key);

    public IClientStreamOutbound? ResolveClientStream(string? key = null) => _outbounds.ResolveClientStream(key);

    public bool TryGetUnary(string? key, out IUnaryOutbound? outbound) =>
        _outbounds.TryGetUnary(key, out outbound);

    public bool TryGetOneway(string? key, out IOnewayOutbound? outbound) =>
        _outbounds.TryGetOneway(key, out outbound);

    public bool TryGetStream(string? key, out IStreamOutbound? outbound) =>
        _outbounds.TryGetStream(key, out outbound);

    public bool TryGetClientStream(string? key, out IClientStreamOutbound? outbound) =>
        _outbounds.TryGetClientStream(key, out outbound);
}
