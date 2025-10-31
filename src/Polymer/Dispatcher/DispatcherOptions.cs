using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;

namespace Polymer.Dispatcher;

public sealed class DispatcherOptions
{
    private readonly List<DispatcherLifecycleComponent> _componentDescriptors = new();
    private readonly List<DispatcherLifecycleComponent> _uniqueComponents = new();
    private readonly HashSet<ILifecycle> _uniqueComponentSet = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, OutboundCollectionBuilder> _outboundBuilders =
        new(StringComparer.OrdinalIgnoreCase);

    public DispatcherOptions(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name cannot be null or whitespace.", nameof(serviceName));
        }

        ServiceName = serviceName;
    }

    public string ServiceName { get; }

    public IList<IUnaryInboundMiddleware> UnaryInboundMiddleware { get; } = new List<IUnaryInboundMiddleware>();
    public IList<IOnewayInboundMiddleware> OnewayInboundMiddleware { get; } = new List<IOnewayInboundMiddleware>();
    public IList<IStreamInboundMiddleware> StreamInboundMiddleware { get; } = new List<IStreamInboundMiddleware>();
    public IList<IUnaryOutboundMiddleware> UnaryOutboundMiddleware { get; } = new List<IUnaryOutboundMiddleware>();
    public IList<IOnewayOutboundMiddleware> OnewayOutboundMiddleware { get; } = new List<IOnewayOutboundMiddleware>();
    public IList<IStreamOutboundMiddleware> StreamOutboundMiddleware { get; } = new List<IStreamOutboundMiddleware>();

    internal IReadOnlyList<DispatcherLifecycleComponent> ComponentDescriptors => _componentDescriptors;
    internal IReadOnlyList<DispatcherLifecycleComponent> UniqueComponents => _uniqueComponents;
    internal IReadOnlyDictionary<string, OutboundCollectionBuilder> OutboundBuilders => _outboundBuilders;

    public void AddTransport(ITransport transport)
    {
        if (transport is null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        AddLifecycle(transport.Name, transport);
    }

    public void AddLifecycle(string name, ILifecycle lifecycle)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Lifecycle name cannot be null or whitespace.", nameof(name));
        }

        if (lifecycle is null)
        {
            throw new ArgumentNullException(nameof(lifecycle));
        }

        var component = new DispatcherLifecycleComponent(name, lifecycle);
        _componentDescriptors.Add(component);

        if (_uniqueComponentSet.Add(lifecycle))
        {
            _uniqueComponents.Add(component);
        }
    }

    public void AddUnaryOutbound(string service, string? key, IUnaryOutbound outbound)
    {
        if (outbound is null)
        {
            throw new ArgumentNullException(nameof(outbound));
        }

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddUnary(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "unary"), outbound);
    }

    public void AddOnewayOutbound(string service, string? key, IOnewayOutbound outbound)
    {
        if (outbound is null)
        {
            throw new ArgumentNullException(nameof(outbound));
        }

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddOneway(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "oneway"), outbound);
    }

    public void AddStreamOutbound(string service, string? key, IStreamOutbound outbound)
    {
        if (outbound is null)
        {
            throw new ArgumentNullException(nameof(outbound));
        }

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddStream(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "stream"), outbound);
    }

    private OutboundCollectionBuilder GetOrCreateOutboundBuilder(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service identifier cannot be null or whitespace.", nameof(service));
        }

        if (!_outboundBuilders.TryGetValue(service, out var builder))
        {
            builder = new OutboundCollectionBuilder(service);
            _outboundBuilders.Add(service, builder);
        }

        return builder;
    }

    private static string BuildOutboundComponentName(string service, string? key, string kind)
    {
        var variant = string.IsNullOrWhiteSpace(key) ? OutboundCollection.DefaultKey : key;
        return $"{service}:{variant}:{kind}";
    }

    internal sealed record DispatcherLifecycleComponent(string Name, ILifecycle Lifecycle);

    internal sealed class OutboundCollectionBuilder
    {
        private readonly string _service;
        private readonly Dictionary<string, IUnaryOutbound> _unary = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IOnewayOutbound> _oneway = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IStreamOutbound> _stream = new(StringComparer.OrdinalIgnoreCase);

        public OutboundCollectionBuilder(string service)
        {
            _service = service;
        }

        public void AddUnary(string? key, IUnaryOutbound outbound)
        {
            var normalized = NormalizeKey(key);

            if (_unary.ContainsKey(normalized))
            {
                throw new InvalidOperationException($"Unary outbound '{normalized}' already registered for service '{_service}'.");
            }

            _unary[normalized] = outbound;
        }

        public void AddOneway(string? key, IOnewayOutbound outbound)
        {
            var normalized = NormalizeKey(key);

            if (_oneway.ContainsKey(normalized))
            {
                throw new InvalidOperationException($"Oneway outbound '{normalized}' already registered for service '{_service}'.");
            }

            _oneway[normalized] = outbound;
        }

        public void AddStream(string? key, IStreamOutbound outbound)
        {
            var normalized = NormalizeKey(key);

            if (_stream.ContainsKey(normalized))
            {
                throw new InvalidOperationException($"Stream outbound '{normalized}' already registered for service '{_service}'.");
            }

            _stream[normalized] = outbound;
        }

        public OutboundCollection Build()
        {
            var unary = _unary.Count == 0
                ? ImmutableDictionary<string, IUnaryOutbound>.Empty
                : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, _unary);

            var oneway = _oneway.Count == 0
                ? ImmutableDictionary<string, IOnewayOutbound>.Empty
                : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, _oneway);

            var stream = _stream.Count == 0
                ? ImmutableDictionary<string, IStreamOutbound>.Empty
                : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, _stream);

            return new OutboundCollection(_service, unary, oneway, stream);
        }

        private static string NormalizeKey(string? key) =>
            string.IsNullOrWhiteSpace(key) ? OutboundCollection.DefaultKey : key!;
    }
}
