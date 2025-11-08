using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Grpc.Interceptors;
using OmniRelay.Transport.Http;
using OmniRelay.Transport.Http.Middleware;
using static Hugo.Go;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Central runtime that registers procedures, composes middleware, manages transports, and dispatches RPC calls.
/// </summary>
public sealed class Dispatcher
{
    private readonly string _serviceName;
    private readonly ProcedureRegistry _procedures = new();
    private readonly ImmutableArray<DispatcherOptions.DispatcherLifecycleComponent> _lifecycleDescriptors;
    private readonly ImmutableArray<DispatcherOptions.DispatcherLifecycleComponent> _lifecycleStartOrder;
    private readonly ImmutableDictionary<string, OutboundCollection> _outbounds;
    private readonly ImmutableArray<IUnaryInboundMiddleware> _inboundUnaryMiddleware;
    private readonly ImmutableArray<IOnewayInboundMiddleware> _inboundOnewayMiddleware;
    private readonly ImmutableArray<IStreamInboundMiddleware> _inboundStreamMiddleware;
    private readonly ImmutableArray<IClientStreamInboundMiddleware> _inboundClientStreamMiddleware;
    private readonly ImmutableArray<IDuplexInboundMiddleware> _inboundDuplexMiddleware;
    private readonly ImmutableArray<IUnaryOutboundMiddleware> _outboundUnaryMiddleware;
    private readonly ImmutableArray<IOnewayOutboundMiddleware> _outboundOnewayMiddleware;
    private readonly ImmutableArray<IStreamOutboundMiddleware> _outboundStreamMiddleware;
    private readonly ImmutableArray<IClientStreamOutboundMiddleware> _outboundClientStreamMiddleware;
    private readonly ImmutableArray<IDuplexOutboundMiddleware> _outboundDuplexMiddleware;
    private readonly Lock _stateLock = new();
    private DispatcherStatus _status = DispatcherStatus.Created;
    private readonly HttpOutboundMiddlewareRegistry? _httpOutboundMiddlewareRegistry;
    private readonly GrpcClientInterceptorRegistry? _grpcClientInterceptorRegistry;
    private readonly GrpcServerInterceptorRegistry? _grpcServerInterceptorRegistry;

    /// <summary>
    /// Creates a dispatcher for a specific service using the provided options.
    /// </summary>
    public Dispatcher(DispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _serviceName = options.ServiceName;
        _lifecycleDescriptors = [.. options.ComponentDescriptors];
        _lifecycleStartOrder = [.. options.UniqueComponents];
        _outbounds = BuildOutboundCollections(options.OutboundBuilders);

        _inboundUnaryMiddleware = [.. options.UnaryInboundMiddleware];
        _inboundOnewayMiddleware = [.. options.OnewayInboundMiddleware];
        _inboundStreamMiddleware = [.. options.StreamInboundMiddleware];
        _inboundClientStreamMiddleware = [.. options.ClientStreamInboundMiddleware];
        _inboundDuplexMiddleware = [.. options.DuplexInboundMiddleware];
        _outboundUnaryMiddleware = [.. options.UnaryOutboundMiddleware];
        _outboundOnewayMiddleware = [.. options.OnewayOutboundMiddleware];
        _outboundStreamMiddleware = [.. options.StreamOutboundMiddleware];
        _outboundClientStreamMiddleware = [.. options.ClientStreamOutboundMiddleware];
        _outboundDuplexMiddleware = [.. options.DuplexOutboundMiddleware];
        Codecs = new CodecRegistry(_serviceName, options.CodecRegistrations);

        BindDispatcherAwareComponents(_lifecycleDescriptors);
        _httpOutboundMiddlewareRegistry = options.HttpOutboundMiddleware.Build();
        _grpcClientInterceptorRegistry = options.GrpcInterceptors.BuildClientRegistry();
        _grpcServerInterceptorRegistry = options.GrpcInterceptors.BuildServerRegistry();
        AttachTransportExtensions();
    }

    /// <summary>Gets the service name this dispatcher serves.</summary>
    public string ServiceName => _serviceName;

    /// <summary>Gets the current lifecycle status.</summary>
    public DispatcherStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return _status;
            }
        }
    }

    /// <summary>Gets globally configured inbound unary middleware.</summary>
    public IReadOnlyList<IUnaryInboundMiddleware> UnaryInboundMiddleware => _inboundUnaryMiddleware;
    public IReadOnlyList<IOnewayInboundMiddleware> OnewayInboundMiddleware => _inboundOnewayMiddleware;
    public IReadOnlyList<IStreamInboundMiddleware> StreamInboundMiddleware => _inboundStreamMiddleware;
    public IReadOnlyList<IClientStreamInboundMiddleware> ClientStreamInboundMiddleware => _inboundClientStreamMiddleware;
    public IReadOnlyList<IDuplexInboundMiddleware> DuplexInboundMiddleware => _inboundDuplexMiddleware;
    public IReadOnlyList<IUnaryOutboundMiddleware> UnaryOutboundMiddleware => _outboundUnaryMiddleware;
    public IReadOnlyList<IOnewayOutboundMiddleware> OnewayOutboundMiddleware => _outboundOnewayMiddleware;
    public IReadOnlyList<IStreamOutboundMiddleware> StreamOutboundMiddleware => _outboundStreamMiddleware;
    public IReadOnlyList<IClientStreamOutboundMiddleware> ClientStreamOutboundMiddleware => _outboundClientStreamMiddleware;
    public IReadOnlyList<IDuplexOutboundMiddleware> DuplexOutboundMiddleware => _outboundDuplexMiddleware;
    /// <summary>Gets the codec registry for inbound and outbound procedures.</summary>
    public CodecRegistry Codecs { get; }

    /// <summary>
    /// Registers a fully constructed <see cref="ProcedureSpec"/> with the dispatcher.
    /// </summary>
    public void Register(ProcedureSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!string.Equals(spec.Service, _serviceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Procedure '{spec.FullName}' does not match dispatcher service '{_serviceName}'.");
        }

        _procedures.Register(spec);
    }

    /// <summary>
    /// Registers a unary procedure with an optional per-procedure middleware/encoding configuration.
    /// </summary>
    public void RegisterUnary(string name, UnaryInboundDelegate handler, Action<UnaryProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new UnaryProcedureBuilder(handler);
        configure?.Invoke(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a unary procedure using a builder configuration.
    /// </summary>
    public void RegisterUnary(string name, Action<UnaryProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new UnaryProcedureBuilder();
        configure(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a oneway procedure with an optional per-procedure configuration.
    /// </summary>
    public void RegisterOneway(string name, OnewayInboundDelegate handler, Action<OnewayProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new OnewayProcedureBuilder(handler);
        configure?.Invoke(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a oneway procedure using a builder configuration.
    /// </summary>
    public void RegisterOneway(string name, Action<OnewayProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new OnewayProcedureBuilder();
        configure(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a server-streaming procedure with an optional per-procedure configuration.
    /// </summary>
    public void RegisterStream(string name, StreamInboundDelegate handler, Action<StreamProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new StreamProcedureBuilder(handler);
        configure?.Invoke(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a server-streaming procedure using a builder configuration.
    /// </summary>
    public void RegisterStream(string name, Action<StreamProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new StreamProcedureBuilder();
        configure(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a client-streaming procedure with an optional per-procedure configuration.
    /// </summary>
    public void RegisterClientStream(string name, ClientStreamInboundDelegate handler, Action<ClientStreamProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new ClientStreamProcedureBuilder(handler);
        configure?.Invoke(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a client-streaming procedure using a builder configuration.
    /// </summary>
    public void RegisterClientStream(string name, Action<ClientStreamProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ClientStreamProcedureBuilder();
        configure(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a duplex-streaming procedure with an optional per-procedure configuration.
    /// </summary>
    public void RegisterDuplex(string name, DuplexInboundDelegate handler, Action<DuplexProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new DuplexProcedureBuilder(handler);
        configure?.Invoke(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>
    /// Registers a duplex-streaming procedure using a builder configuration.
    /// </summary>
    public void RegisterDuplex(string name, Action<DuplexProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new DuplexProcedureBuilder();
        configure(builder);
        Register(builder.Build(_serviceName, EnsureProcedureName(name)));
    }

    /// <summary>Attempts to get a procedure by name and kind.</summary>
    public bool TryGetProcedure(string name, ProcedureKind kind, out ProcedureSpec spec) =>
        _procedures.TryGet(_serviceName, name, kind, out spec);

    /// <summary>Returns a snapshot of all registered procedures.</summary>
    public IReadOnlyCollection<ProcedureSpec> ListProcedures() =>
        _procedures.Snapshot();

    /// <summary>
    /// Invokes a registered unary procedure through the inbound middleware pipeline.
    /// </summary>
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeUnaryAsync(
        string procedure,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.Unary, out var spec) ||
            spec is not UnaryProcedureSpec unarySpec)
        {
            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Unimplemented,
                $"Unary procedure '{procedure}' is not registered for service '{_serviceName}'.",
                transport: request.Meta.Transport ?? "unknown");

            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
        }

        var pipeline = MiddlewareComposer.ComposeUnaryInbound(
            CombineMiddleware(_inboundUnaryMiddleware, unarySpec.Middleware),
            unarySpec.Handler);

        return pipeline(request, cancellationToken);
    }

    /// <summary>
    /// Invokes a registered oneway procedure through the inbound middleware pipeline.
    /// </summary>
    public ValueTask<Result<OnewayAck>> InvokeOnewayAsync(
        string procedure,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.Oneway, out var spec) ||
            spec is not OnewayProcedureSpec onewaySpec)
        {
            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Unimplemented,
                $"Oneway procedure '{procedure}' is not registered for service '{_serviceName}'.",
                transport: request.Meta.Transport ?? "unknown");

            return ValueTask.FromResult(Err<OnewayAck>(error));
        }

        var pipeline = MiddlewareComposer.ComposeOnewayInbound(
            CombineMiddleware(_inboundOnewayMiddleware, onewaySpec.Middleware),
            onewaySpec.Handler);

        return pipeline(request, cancellationToken);
    }

    /// <summary>
    /// Returns the client configuration and outbound bindings for a remote service.
    /// </summary>
    public ClientConfiguration ClientConfig(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service identifier cannot be null or whitespace.", nameof(service));
        }

        if (!_outbounds.TryGetValue(service, out var collection))
        {
            if (string.Equals(service, _serviceName, StringComparison.OrdinalIgnoreCase))
            {
                collection = new OutboundCollection(
                    service,
                    ImmutableDictionary<string, IUnaryOutbound>.Empty,
                    ImmutableDictionary<string, IOnewayOutbound>.Empty,
                    ImmutableDictionary<string, IStreamOutbound>.Empty,
                    ImmutableDictionary<string, IClientStreamOutbound>.Empty,
                    ImmutableDictionary<string, IDuplexOutbound>.Empty);
            }
            else
            {
                throw new KeyNotFoundException($"No outbound configuration found for service '{service}'.");
            }
        }

        return new ClientConfiguration(
            collection,
            _outboundUnaryMiddleware,
            _outboundOnewayMiddleware,
            _outboundStreamMiddleware,
            _outboundClientStreamMiddleware,
            _outboundDuplexMiddleware);
    }

    /// <summary>
    /// Invokes a registered server-streaming procedure through the inbound middleware pipeline.
    /// </summary>
    public ValueTask<Result<IStreamCall>> InvokeStreamAsync(
        string procedure,
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(procedure))
        {
            return ValueTask.FromResult(Err<IStreamCall>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "Procedure name is required for streaming calls.")));
        }

        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.Stream, out var spec) ||
            spec is not StreamProcedureSpec streamSpec)
        {
            return ValueTask.FromResult(Err<IStreamCall>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Unimplemented,
                $"Stream procedure '{procedure}' is not registered for service '{_serviceName}'.")));
        }

        var middleware = CombineMiddleware(_inboundStreamMiddleware, streamSpec.Middleware);
        var pipeline = MiddlewareComposer.ComposeStreamInbound(middleware, streamSpec.Handler);
        return pipeline(request, options, cancellationToken);
    }

    /// <summary>
    /// Invokes a registered duplex-streaming procedure through the inbound middleware pipeline.
    /// </summary>
    public ValueTask<Result<IDuplexStreamCall>> InvokeDuplexAsync(
        string procedure,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(procedure))
        {
            return ValueTask.FromResult(Err<IDuplexStreamCall>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "Procedure name is required for duplex streaming calls.")));
        }

        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.Duplex, out var spec) ||
            spec is not DuplexProcedureSpec duplexSpec)
        {
            return ValueTask.FromResult(Err<IDuplexStreamCall>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Unimplemented,
                $"Duplex stream procedure '{procedure}' is not registered for service '{_serviceName}'.")));
        }

        var middleware = CombineMiddleware(_inboundDuplexMiddleware, duplexSpec.Middleware);
        var pipeline = MiddlewareComposer.ComposeDuplexInbound(middleware, duplexSpec.Handler);
        return pipeline(request, cancellationToken);
    }

    /// <summary>
    /// Starts a client-streaming procedure and returns a call handle to write request messages.
    /// </summary>
    public ValueTask<Result<ClientStreamCall>> InvokeClientStreamAsync(
        string procedure,
        RequestMeta requestMeta,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(procedure))
        {
            return ValueTask.FromResult(Err<ClientStreamCall>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "Procedure name is required for client streaming calls.")));
        }

        ArgumentNullException.ThrowIfNull(requestMeta);

        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.ClientStream, out var spec) ||
            spec is not ClientStreamProcedureSpec clientStreamSpec)
        {
            return ValueTask.FromResult(Err<ClientStreamCall>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Unimplemented,
                $"Client stream procedure '{procedure}' is not registered for service '{_serviceName}'.",
                transport: requestMeta.Transport ?? "unknown")));
        }

        var call = ClientStreamCall.Create(requestMeta);
        var context = new ClientStreamRequestContext(call.RequestMeta, call.Reader);

        var middleware = CombineMiddleware(_inboundClientStreamMiddleware, clientStreamSpec.Middleware);
        var pipeline = MiddlewareComposer.ComposeClientStreamInbound(middleware, clientStreamSpec.Handler);

        _ = ProcessClientStreamAsync(call, context, pipeline, cancellationToken);

        return ValueTask.FromResult(Ok(call));
    }

    /// <summary>
    /// Starts all dispatcher lifecycle components in parallel.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        BeginStart();

        try
        {
            if (_lifecycleStartOrder.Length == 0)
            {
                CompleteStart();
                return;
            }

            var startedComponents = new int[_lifecycleStartOrder.Length];
            var wg = new WaitGroup();
            var exceptions = new ConcurrentQueue<Exception>();

            foreach (var (component, index) in _lifecycleStartOrder.Select((component, index) => (component, index)))
            {
                var lifecycle = component.Lifecycle;

                wg.Go(async token =>
                {
                    try
                    {
                        await lifecycle.StartAsync(token).ConfigureAwait(false);
                        Volatile.Write(ref startedComponents[index], 1);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                }, cancellationToken);
            }

            try
            {
                await wg.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception waitException)
            {
                try
                {
                    await RollbackStartedComponentsAsync(startedComponents).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(waitException, rollbackException);
                }

                throw;
            }

            if (!exceptions.IsEmpty)
            {
                var startException = CreateStartFailureException(exceptions);

                try
                {
                    await RollbackStartedComponentsAsync(startedComponents).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(startException, rollbackException);
                }

                throw startException;
            }
            CompleteStart();
        }
        catch
        {
            lock (_stateLock)
            {
                _status = DispatcherStatus.Stopped;
            }

            throw;
        }
    }

    /// <summary>
    /// Stops all dispatcher lifecycle components in reverse start order.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!BeginStop())
        {
            return;
        }

        var exceptions = new List<Exception>();

        for (var index = _lifecycleStartOrder.Length - 1; index >= 0; index--)
        {
            var component = _lifecycleStartOrder[index];
            try
            {
                await component.Lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        if (exceptions.Count == 1)
        {
            FailStop();
            throw exceptions[0];
        }

        if (exceptions.Count > 1)
        {
            FailStop();
            throw new AggregateException("One or more dispatcher components failed to stop.", exceptions);
        }

        CompleteStop();
    }

    /// <summary>
    /// Produces a snapshot of registered procedures, middleware, components, and outbounds for diagnostics.
    /// </summary>
    public DispatcherIntrospection Introspect()
    {
        var procedureSnapshot = _procedures.Snapshot();

        var unaryProcedures = procedureSnapshot
            .OfType<UnaryProcedureSpec>()
            .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static spec => new ProcedureDescriptor(spec.Name, spec.Encoding, spec.Aliases))
            .ToImmutableArray();

        var onewayProcedures = procedureSnapshot
            .OfType<OnewayProcedureSpec>()
            .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static spec => new ProcedureDescriptor(spec.Name, spec.Encoding, spec.Aliases))
            .ToImmutableArray();

        var streamProcedures = procedureSnapshot
            .OfType<StreamProcedureSpec>()
            .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static spec => new StreamProcedureDescriptor(spec.Name, spec.Encoding, spec.Aliases, spec.Metadata))
            .ToImmutableArray();

        var clientStreamProcedures = procedureSnapshot
            .OfType<ClientStreamProcedureSpec>()
            .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static spec => new ClientStreamProcedureDescriptor(spec.Name, spec.Encoding, spec.Aliases, spec.Metadata))
            .ToImmutableArray();

        var duplexProcedures = procedureSnapshot
            .OfType<DuplexProcedureSpec>()
            .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static spec => new DuplexProcedureDescriptor(spec.Name, spec.Encoding, spec.Aliases, spec.Metadata))
            .ToImmutableArray();

        var procedures = new ProcedureGroups(
            unaryProcedures,
            onewayProcedures,
            streamProcedures,
            clientStreamProcedures,
            duplexProcedures);

        var components = _lifecycleDescriptors
            .Select(static component =>
                new LifecycleComponentDescriptor(
                    component.Name,
                    component.Lifecycle.GetType().FullName ?? component.Lifecycle.GetType().Name))
            .ToImmutableArray();

        var outbounds = _outbounds.Values
            .OrderBy(static collection => collection.Service, StringComparer.OrdinalIgnoreCase)
            .Select(collection =>
                new OutboundDescriptor(
                    collection.Service,
                    DescribeOutbounds(collection.Unary),
                    DescribeOutbounds(collection.Oneway),
                    DescribeOutbounds(collection.Stream),
                    DescribeOutbounds(collection.ClientStream),
                    DescribeOutbounds(collection.Duplex)))
            .ToImmutableArray();

        var middleware = new MiddlewareSummary(
            [.. _inboundUnaryMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _inboundOnewayMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _inboundStreamMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _inboundClientStreamMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _inboundDuplexMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundUnaryMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundOnewayMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundStreamMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundClientStreamMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundDuplexMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)]);

        return new DispatcherIntrospection(
            _serviceName,
            Status,
            procedures,
            components,
            outbounds,
            middleware);
    }

    private static ImmutableArray<OutboundBindingDescriptor> DescribeOutbounds<TOutbound>(IReadOnlyDictionary<string, TOutbound> source)
        where TOutbound : class
    {
        if (source.Count == 0)
        {
            return [];
        }

        return [.. source
            .OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static kvp =>
            {
                var outbound = kvp.Value!;
                object? diagnostics = outbound is IOutboundDiagnostic diagnostic
                    ? diagnostic.GetOutboundDiagnostics()
                    : null;

                var implementation = outbound.GetType().FullName ?? outbound.GetType().Name;
                return new OutboundBindingDescriptor(kvp.Key, implementation, diagnostics);
            })];
    }

    private static async Task ProcessClientStreamAsync(
        ClientStreamCall call,
        ClientStreamRequestContext context,
        ClientStreamInboundDelegate pipeline,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await pipeline(context, cancellationToken).ConfigureAwait(false);
            call.TryComplete(result);
        }
        catch (Exception ex)
        {
            var transport = call.RequestMeta.Transport ?? "unknown";
            var failure = OmniRelayErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, transport);
            call.TryComplete(failure);
        }
    }

    private void BeginStart()
    {
        lock (_stateLock)
        {
            switch (_status)
            {
                case DispatcherStatus.Created:
                case DispatcherStatus.Stopped:
                    _status = DispatcherStatus.Starting;
                    return;
                case DispatcherStatus.Starting:
                case DispatcherStatus.Running:
                    throw new InvalidOperationException("Dispatcher is already running or starting.");
                case DispatcherStatus.Stopping:
                    throw new InvalidOperationException("Dispatcher is stopping.");
                default:
                    throw new InvalidOperationException($"Dispatcher cannot start from state {_status}.");
            }
        }
    }

    private bool BeginStop()
    {
        lock (_stateLock)
        {
            switch (_status)
            {
                case DispatcherStatus.Created:
                case DispatcherStatus.Stopped:
                    _status = DispatcherStatus.Stopped;
                    return false;
                case DispatcherStatus.Starting:
                    throw new InvalidOperationException("Dispatcher is starting. Await StartAsync completion before stopping.");
                case DispatcherStatus.Stopping:
                    return false;
                default:
                    _status = DispatcherStatus.Stopping;
                    return true;
            }
        }
    }

    private void CompleteStart()
    {
        lock (_stateLock)
        {
            _status = DispatcherStatus.Running;
        }
    }

    private void CompleteStop()
    {
        lock (_stateLock)
        {
            _status = DispatcherStatus.Stopped;
        }
    }

    private void FailStop()
    {
        lock (_stateLock)
        {
            _status = DispatcherStatus.Running;
        }
    }

    private async Task RollbackStartedComponentsAsync(int[] startedComponents)
    {
        if (_lifecycleStartOrder.Length == 0 || startedComponents.Length == 0)
        {
            return;
        }

        var rollbackExceptions = new List<Exception>();

        for (var index = _lifecycleStartOrder.Length - 1; index >= 0; index--)
        {
            if (Volatile.Read(ref startedComponents[index]) == 0)
            {
                continue;
            }

            var lifecycle = _lifecycleStartOrder[index].Lifecycle;

            try
            {
                await lifecycle.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                rollbackExceptions.Add(ex);
            }
        }

        if (rollbackExceptions.Count == 1)
        {
            throw rollbackExceptions[0];
        }

        if (rollbackExceptions.Count > 1)
        {
            throw new AggregateException("One or more dispatcher components failed to roll back after a start failure.", rollbackExceptions);
        }
    }

    private static Exception CreateStartFailureException(ConcurrentQueue<Exception> exceptions)
    {
        if (exceptions.Count == 1 && exceptions.TryPeek(out var single))
        {
            return single;
        }

        return new AggregateException(exceptions);
    }

    private static ImmutableDictionary<string, OutboundCollection> BuildOutboundCollections(
        IReadOnlyDictionary<string, DispatcherOptions.OutboundCollectionBuilder> builders)
    {
        if (builders.Count == 0)
        {
            return [];
        }

        var map = ImmutableDictionary.CreateBuilder<string, OutboundCollection>(StringComparer.OrdinalIgnoreCase);

        foreach (var (service, builder) in builders)
        {
            map[service] = builder.Build();
        }

        return map.ToImmutable();
    }

    private void BindDispatcherAwareComponents(ImmutableArray<DispatcherOptions.DispatcherLifecycleComponent> components)
    {
        foreach (var component in components)
        {
            if (component.Lifecycle is IDispatcherAware aware)
            {
                aware.Bind(this);
            }
        }
    }

    private void AttachTransportExtensions()
    {
        if (_outbounds.Count > 0)
        {
            if (_httpOutboundMiddlewareRegistry is not null)
            {
                AttachHttpOutboundMiddleware(_httpOutboundMiddlewareRegistry);
            }

            if (_grpcClientInterceptorRegistry is not null)
            {
                AttachGrpcClientInterceptors(_grpcClientInterceptorRegistry);
            }
        }

        if (_grpcServerInterceptorRegistry is not null)
        {
            AttachGrpcServerInterceptors(_grpcServerInterceptorRegistry);
        }
    }

    private void AttachHttpOutboundMiddleware(HttpOutboundMiddlewareRegistry registry)
    {
        var attached = new HashSet<IHttpOutboundMiddlewareSink>(ReferenceEqualityComparer.Instance);

        foreach (var (service, collection) in _outbounds)
        {
            Attach(collection.Unary.Values, service);
            Attach(collection.Oneway.Values, service);
            Attach(collection.Stream.Values, service);
            Attach(collection.ClientStream.Values, service);
            Attach(collection.Duplex.Values, service);
        }

        void Attach(IEnumerable<object> outbounds, string service)
        {
            foreach (var outbound in outbounds)
            {
                if (outbound is IHttpOutboundMiddlewareSink sink && attached.Add(sink))
                {
                    sink.Attach(service, registry);
                }
            }
        }
    }

    private void AttachGrpcClientInterceptors(GrpcClientInterceptorRegistry registry)
    {
        var attached = new HashSet<IGrpcClientInterceptorSink>(ReferenceEqualityComparer.Instance);

        foreach (var (service, collection) in _outbounds)
        {
            Attach(collection.Unary.Values, service);
            Attach(collection.Oneway.Values, service);
            Attach(collection.Stream.Values, service);
            Attach(collection.ClientStream.Values, service);
            Attach(collection.Duplex.Values, service);
        }

        void Attach(IEnumerable<object> outbounds, string service)
        {
            foreach (var outbound in outbounds)
            {
                if (outbound is IGrpcClientInterceptorSink sink && attached.Add(sink))
                {
                    sink.AttachGrpcClientInterceptors(service, registry);
                }
            }
        }
    }

    private void AttachGrpcServerInterceptors(GrpcServerInterceptorRegistry registry)
    {
        var attached = new HashSet<IGrpcServerInterceptorSink>(ReferenceEqualityComparer.Instance);

        foreach (var component in _lifecycleDescriptors)
        {
            if (component.Lifecycle is IGrpcServerInterceptorSink sink && attached.Add(sink))
            {
                sink.AttachGrpcServerInterceptors(registry);
            }
        }
    }

    private static IReadOnlyList<TMiddleware> CombineMiddleware<TMiddleware>(
        ImmutableArray<TMiddleware> global,
        IReadOnlyList<TMiddleware> local)
    {
        if (global.Length == 0)
        {
            return local;
        }

        if (local.Count == 0)
        {
            return global;
        }

        var combined = new TMiddleware[global.Length + local.Count];
        global.AsSpan().CopyTo(combined);
        for (var index = 0; index < local.Count; index++)
        {
            combined[global.Length + index] = local[index];
        }

        return combined;
    }

    private static string EnsureProcedureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Procedure name cannot be null or whitespace.", nameof(name));
        }

        return name.Trim();
    }
}
