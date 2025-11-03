using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Polymer.Core;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;
using Polymer.Transport.Http.Middleware;
using Polymer.Transport.Grpc.Interceptors;

namespace Polymer.Dispatcher;

public sealed class DispatcherOptions
{
    private readonly List<DispatcherLifecycleComponent> _componentDescriptors = [];
    private readonly List<DispatcherLifecycleComponent> _uniqueComponents = [];
    private readonly HashSet<ILifecycle> _uniqueComponentSet = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, OutboundCollectionBuilder> _outboundBuilders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProcedureCodecRegistration> _codecRegistrations = [];

    public DispatcherOptions(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name cannot be null or whitespace.", nameof(serviceName));
        }

        ServiceName = serviceName;
    }

    public string ServiceName { get; }

    public IList<IUnaryInboundMiddleware> UnaryInboundMiddleware { get; } = [];
    public IList<IOnewayInboundMiddleware> OnewayInboundMiddleware { get; } = [];
    public IList<IStreamInboundMiddleware> StreamInboundMiddleware { get; } = [];
    public IList<IClientStreamInboundMiddleware> ClientStreamInboundMiddleware { get; } = [];
    public IList<IUnaryOutboundMiddleware> UnaryOutboundMiddleware { get; } = [];
    public IList<IOnewayOutboundMiddleware> OnewayOutboundMiddleware { get; } = [];
    public IList<IStreamOutboundMiddleware> StreamOutboundMiddleware { get; } = [];
    public IList<IDuplexInboundMiddleware> DuplexInboundMiddleware { get; } = [];
    public IList<IDuplexOutboundMiddleware> DuplexOutboundMiddleware { get; } = [];
    public IList<IClientStreamOutboundMiddleware> ClientStreamOutboundMiddleware { get; } = [];
    public HttpOutboundMiddlewareBuilder HttpOutboundMiddleware { get; } = new();
    public GrpcTransportInterceptorBuilder GrpcInterceptors { get; } = new();

    internal IReadOnlyList<DispatcherLifecycleComponent> ComponentDescriptors => _componentDescriptors;
    internal IReadOnlyList<DispatcherLifecycleComponent> UniqueComponents => _uniqueComponents;
    internal IReadOnlyDictionary<string, OutboundCollectionBuilder> OutboundBuilders => _outboundBuilders;
    internal IReadOnlyList<ProcedureCodecRegistration> CodecRegistrations => _codecRegistrations;

    public void AddTransport(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        AddLifecycle(transport.Name, transport);
    }

    public void AddLifecycle(string name, ILifecycle lifecycle)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Lifecycle name cannot be null or whitespace.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(lifecycle);

        var component = new DispatcherLifecycleComponent(name, lifecycle);
        _componentDescriptors.Add(component);

        if (_uniqueComponentSet.Add(lifecycle))
        {
            _uniqueComponents.Add(component);
        }
    }

    public void AddUnaryOutbound(string service, string? key, IUnaryOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddUnary(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "unary"), outbound);
    }

    public void AddOnewayOutbound(string service, string? key, IOnewayOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddOneway(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "oneway"), outbound);
    }

    public void AddStreamOutbound(string service, string? key, IStreamOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddStream(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "stream"), outbound);
    }

    public void AddClientStreamOutbound(string service, string? key, IClientStreamOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddClientStream(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "client-stream"), outbound);
    }

    public void AddDuplexOutbound(string service, string? key, IDuplexOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddDuplex(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "duplex-stream"), outbound);
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

    public void AddInboundUnaryCodec<TRequest, TResponse>(string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.Unary, codec, aliases);

    public void AddInboundOnewayCodec<TRequest>(string procedure, ICodec<TRequest, object> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.Oneway, codec, aliases);

    public void AddInboundStreamCodec<TRequest, TResponse>(string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.Stream, codec, aliases);

    public void AddInboundClientStreamCodec<TRequest, TResponse>(string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.ClientStream, codec, aliases);

    public void AddInboundDuplexCodec<TRequest, TResponse>(string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.Duplex, codec, aliases);

    public void AddOutboundUnaryCodec<TRequest, TResponse>(string service, string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.Unary, codec, aliases);

    public void AddOutboundOnewayCodec<TRequest>(string service, string procedure, ICodec<TRequest, object> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.Oneway, codec, aliases);

    public void AddOutboundStreamCodec<TRequest, TResponse>(string service, string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.Stream, codec, aliases);

    public void AddOutboundClientStreamCodec<TRequest, TResponse>(string service, string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.ClientStream, codec, aliases);

    public void AddOutboundDuplexCodec<TRequest, TResponse>(string service, string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.Duplex, codec, aliases);

    private void AddCodec<TRequest, TResponse>(
        ProcedureCodecScope scope,
        string? service,
        string procedure,
        ProcedureKind kind,
        ICodec<TRequest, TResponse> codec,
        IEnumerable<string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(procedure))
        {
            throw new ArgumentException("Procedure name cannot be null or whitespace.", nameof(procedure));
        }

        ArgumentNullException.ThrowIfNull(codec);

        var aliasSnapshot = aliases is null
            ? []
            : ImmutableArray.CreateRange(aliases);

        ValidateAliasSnapshot(aliasSnapshot);

        _codecRegistrations.Add(new ProcedureCodecRegistration(
            scope,
            service,
            procedure.Trim(),
            kind,
            typeof(TRequest),
            typeof(TResponse),
            codec,
            codec.Encoding,
            aliasSnapshot));
    }

    private static void ValidateAliasSnapshot(ImmutableArray<string> aliases)
    {
        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                throw new ArgumentException("Codec aliases cannot contain null or whitespace entries.", nameof(aliases));
            }
        }
    }

    internal sealed record DispatcherLifecycleComponent(string Name, ILifecycle Lifecycle);

    internal sealed class OutboundCollectionBuilder(string service)
    {
        private readonly string _service = service;
        private readonly Dictionary<string, IUnaryOutbound> _unary = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IOnewayOutbound> _oneway = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IStreamOutbound> _stream = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IDuplexOutbound> _duplex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IClientStreamOutbound> _clientStream = new(StringComparer.OrdinalIgnoreCase);

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

        public void AddClientStream(string? key, IClientStreamOutbound outbound)
        {
            var normalized = NormalizeKey(key);

            if (_clientStream.ContainsKey(normalized))
            {
                throw new InvalidOperationException($"Client stream outbound '{normalized}' already registered for service '{_service}'.");
            }

            _clientStream[normalized] = outbound;
        }

        public void AddDuplex(string? key, IDuplexOutbound outbound)
        {
            var normalized = NormalizeKey(key);

            if (_duplex.ContainsKey(normalized))
            {
                throw new InvalidOperationException($"Duplex outbound '{normalized}' already registered for service '{_service}'.");
            }

            _duplex[normalized] = outbound;
        }

        public OutboundCollection Build()
        {
            var unary = _unary.Count == 0
                ? []
                : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, _unary);

            var oneway = _oneway.Count == 0
                ? []
                : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, _oneway);

            var stream = _stream.Count == 0
                ? []
                : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, _stream);

            var clientStream = _clientStream.Count == 0
                ? []
                : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, _clientStream);

            var duplex = _duplex.Count == 0
                ? []
                : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, _duplex);

            return new OutboundCollection(_service, unary, oneway, stream, clientStream, duplex);
        }

        private static string NormalizeKey(string? key) =>
            string.IsNullOrWhiteSpace(key) ? OutboundCollection.DefaultKey : key!;
    }
}
