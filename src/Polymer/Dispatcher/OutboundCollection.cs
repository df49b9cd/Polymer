using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Polymer.Core.Transport;

namespace Polymer.Dispatcher;

public sealed class OutboundCollection
{
    public const string DefaultKey = "default";

    private readonly ImmutableDictionary<string, IUnaryOutbound> _unary;
    private readonly ImmutableDictionary<string, IOnewayOutbound> _oneway;
    private readonly ImmutableDictionary<string, IStreamOutbound> _stream;

    internal OutboundCollection(
        string service,
        ImmutableDictionary<string, IUnaryOutbound> unary,
        ImmutableDictionary<string, IOnewayOutbound> oneway,
        ImmutableDictionary<string, IStreamOutbound> stream)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        ArgumentNullException.ThrowIfNull(unary);
        ArgumentNullException.ThrowIfNull(oneway);
        ArgumentNullException.ThrowIfNull(stream);
        _unary = unary!;
        _oneway = oneway!;
        _stream = stream!;
    }

    public string Service { get; }

    public IReadOnlyDictionary<string, IUnaryOutbound> Unary => _unary;
    public IReadOnlyDictionary<string, IOnewayOutbound> Oneway => _oneway;
    public IReadOnlyDictionary<string, IStreamOutbound> Stream => _stream;

    public bool TryGetUnary(string? key, out IUnaryOutbound? outbound) =>
        _unary.TryGetValue(NormalizeKey(key), out outbound);

    public bool TryGetOneway(string? key, out IOnewayOutbound? outbound) =>
        _oneway.TryGetValue(NormalizeKey(key), out outbound);

    public bool TryGetStream(string? key, out IStreamOutbound? outbound) =>
        _stream.TryGetValue(NormalizeKey(key), out outbound);

    public IUnaryOutbound? ResolveUnary(string? key = null) =>
        _unary.TryGetValue(NormalizeKey(key), out var outbound) ? outbound : null;

    public IOnewayOutbound? ResolveOneway(string? key = null) =>
        _oneway.TryGetValue(NormalizeKey(key), out var outbound) ? outbound : null;

    public IStreamOutbound? ResolveStream(string? key = null) =>
        _stream.TryGetValue(NormalizeKey(key), out var outbound) ? outbound : null;

    private static string NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? DefaultKey : key!;

}
