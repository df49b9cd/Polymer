using System.Collections.Immutable;
using Hugo.Policies;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Transport.Grpc.Interceptors;
using OmniRelay.Transport.Http.Middleware;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Configuration builder for the dispatcher: lifecycle components, outbound bindings, middleware, and codec registrations.
/// </summary>
public sealed class DispatcherOptions
{
    private readonly List<DispatcherLifecycleComponent> _componentDescriptors = [];
    private readonly List<DispatcherLifecycleComponent> _uniqueComponents = [];
    private readonly HashSet<ILifecycle> _uniqueComponentSet = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, OutboundRegistryBuilder> _outboundBuilders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProcedureCodecRegistration> _codecRegistrations = [];

    /// <summary>
    /// Creates options for a dispatcher serving the specified service name.
    /// </summary>
    public DispatcherOptions(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name cannot be null or whitespace.", nameof(serviceName));
        }

        ServiceName = serviceName;
    }

    /// <summary>Gets the service name.</summary>
    public string ServiceName { get; }

    /// <summary>Retry policy applied to lifecycle StartAsync operations.</summary>
    public ResultExecutionPolicy StartRetryPolicy { get; set; } = ResultExecutionPolicy.None;
    /// <summary>Retry policy applied to lifecycle StopAsync operations.</summary>
    public ResultExecutionPolicy StopRetryPolicy { get; set; } = ResultExecutionPolicy.None;

    /// <summary>Global inbound unary middleware.</summary>
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
    /// <summary>HTTP outbound middleware configuration builder.</summary>
    public HttpOutboundMiddlewareBuilder HttpOutboundMiddleware { get; } = new();
    /// <summary>gRPC transport interceptor configuration builder.</summary>
    public GrpcTransportInterceptorBuilder GrpcInterceptors { get; } = new();

    internal IReadOnlyList<DispatcherLifecycleComponent> ComponentDescriptors => _componentDescriptors;
    internal IReadOnlyList<DispatcherLifecycleComponent> UniqueComponents => _uniqueComponents;
    internal IReadOnlyDictionary<string, OutboundRegistryBuilder> OutboundBuilders => _outboundBuilders;
    internal IReadOnlyList<ProcedureCodecRegistration> CodecRegistrations => _codecRegistrations;

    /// <summary>Adds a transport and wires it into dispatcher lifecycle.</summary>
    public void AddTransport(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        AddLifecycle(transport.Name, transport);
    }

    /// <summary>Adds an arbitrary lifecycle component by name.</summary>
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

    /// <summary>Binds a unary outbound under the given service and key.</summary>
    public void AddUnaryOutbound(string service, string? key, IUnaryOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddUnary(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "unary"), outbound);
    }

    /// <summary>Binds a oneway outbound under the given service and key.</summary>
    public void AddOnewayOutbound(string service, string? key, IOnewayOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddOneway(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "oneway"), outbound);
    }

    /// <summary>Binds a server-streaming outbound under the given service and key.</summary>
    public void AddStreamOutbound(string service, string? key, IStreamOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddStream(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "stream"), outbound);
    }

    /// <summary>Binds a client-streaming outbound under the given service and key.</summary>
    public void AddClientStreamOutbound(string service, string? key, IClientStreamOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddClientStream(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "client-stream"), outbound);
    }

    /// <summary>Binds a duplex-streaming outbound under the given service and key.</summary>
    public void AddDuplexOutbound(string service, string? key, IDuplexOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var builder = GetOrCreateOutboundBuilder(service);
        builder.AddDuplex(key, outbound);
        AddLifecycle(BuildOutboundComponentName(service, key, "duplex-stream"), outbound);
    }

    private OutboundRegistryBuilder GetOrCreateOutboundBuilder(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service identifier cannot be null or whitespace.", nameof(service));
        }

        if (!_outboundBuilders.TryGetValue(service, out var builder))
        {
            builder = new OutboundRegistryBuilder(service);
            _outboundBuilders.Add(service, builder);
        }

        return builder;
    }

    private static string BuildOutboundComponentName(string service, string? key, string kind)
    {
        var variant = string.IsNullOrWhiteSpace(key) ? OutboundRegistry.DefaultKey : key;
        return $"{service}:{variant}:{kind}";
    }

    /// <summary>Registers an inbound codec for a unary procedure on the local service.</summary>
    public void AddInboundUnaryCodec<TRequest, TResponse>(string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.Unary, codec, aliases);

    /// <summary>Registers an inbound codec for a oneway procedure on the local service.</summary>
    public void AddInboundOnewayCodec<TRequest>(string procedure, ICodec<TRequest, object> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.Oneway, codec, aliases);

    /// <summary>Registers an inbound codec for a server-streaming procedure on the local service.</summary>
    public void AddInboundStreamCodec<TRequest, TResponse>(string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.Stream, codec, aliases);

    /// <summary>Registers an inbound codec for a client-streaming procedure on the local service.</summary>
    public void AddInboundClientStreamCodec<TRequest, TResponse>(string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.ClientStream, codec, aliases);

    /// <summary>Registers an inbound codec for a duplex-streaming procedure on the local service.</summary>
    public void AddInboundDuplexCodec<TRequest, TResponse>(string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Inbound, null, procedure, ProcedureKind.Duplex, codec, aliases);

    /// <summary>Registers an outbound codec for a unary procedure on a remote service.</summary>
    public void AddOutboundUnaryCodec<TRequest, TResponse>(string service, string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.Unary, codec, aliases);

    /// <summary>Registers an outbound codec for a oneway procedure on a remote service.</summary>
    public void AddOutboundOnewayCodec<TRequest>(string service, string procedure, ICodec<TRequest, object> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.Oneway, codec, aliases);

    /// <summary>Registers an outbound codec for a server-streaming procedure on a remote service.</summary>
    public void AddOutboundStreamCodec<TRequest, TResponse>(string service, string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.Stream, codec, aliases);

    /// <summary>Registers an outbound codec for a client-streaming procedure on a remote service.</summary>
    public void AddOutboundClientStreamCodec<TRequest, TResponse>(string service, string procedure, ICodec<TRequest, TResponse> codec, IEnumerable<string>? aliases = null) =>
        AddCodec(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.ClientStream, codec, aliases);

    /// <summary>Registers an outbound codec for a duplex-streaming procedure on a remote service.</summary>
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

    internal sealed class OutboundRegistryBuilder(string service)
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

        public OutboundRegistry Build()
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

            return new OutboundRegistry(_service, unary, oneway, stream, clientStream, duplex);
        }

        private static string NormalizeKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return OutboundRegistry.DefaultKey;
            }

            return key!.Trim();
        }
    }
}
