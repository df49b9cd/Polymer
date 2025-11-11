using System.Collections.Concurrent;
using System.Collections.Immutable;
using Hugo;
using Hugo.Policies;
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
    private readonly ResultExecutionPolicy _startRetryPolicy;
    private readonly ResultExecutionPolicy _stopRetryPolicy;

    /// <summary>
    /// Creates a dispatcher for a specific service using the provided options.
    /// </summary>
    public Dispatcher(DispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ServiceName = options.ServiceName;
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
        _startRetryPolicy = options.StartRetryPolicy;
        _stopRetryPolicy = options.StopRetryPolicy;
        Codecs = new CodecRegistry(ServiceName, options.CodecRegistrations);

        BindDispatcherAwareComponents(_lifecycleDescriptors);
        _httpOutboundMiddlewareRegistry = options.HttpOutboundMiddleware.Build();
        _grpcClientInterceptorRegistry = options.GrpcInterceptors.BuildClientRegistry();
        _grpcServerInterceptorRegistry = options.GrpcInterceptors.BuildServerRegistry();
        AttachTransportExtensions();
    }

    /// <summary>Gets the service name this dispatcher serves.</summary>
    public string ServiceName { get; }

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
    public Result<Unit> Register(ProcedureSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!string.Equals(spec.Service, ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return Err<Unit>(CreateDispatcherError(
                $"Procedure '{spec.FullName}' does not match dispatcher service '{ServiceName}'.",
                OmniRelayStatusCode.InvalidArgument));
        }

        try
        {
            _procedures.Register(spec);
            return Ok(Unit.Value);
        }
        catch (InvalidOperationException ex)
        {
            return Err<Unit>(CreateDispatcherError(ex.Message, OmniRelayStatusCode.FailedPrecondition));
        }
    }

    /// <summary>
    /// Registers a unary procedure with an optional per-procedure middleware/encoding configuration.
    /// </summary>
    public Result<Unit> RegisterUnary(string name, UnaryInboundDelegate handler, Action<UnaryProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new UnaryProcedureBuilder(handler);
        configure?.Invoke(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a unary procedure using a builder configuration.
    /// </summary>
    public Result<Unit> RegisterUnary(string name, Action<UnaryProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new UnaryProcedureBuilder();
        configure(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a oneway procedure with an optional per-procedure configuration.
    /// </summary>
    public Result<Unit> RegisterOneway(string name, OnewayInboundDelegate handler, Action<OnewayProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new OnewayProcedureBuilder(handler);
        configure?.Invoke(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a oneway procedure using a builder configuration.
    /// </summary>
    public Result<Unit> RegisterOneway(string name, Action<OnewayProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new OnewayProcedureBuilder();
        configure(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a server-streaming procedure with an optional per-procedure configuration.
    /// </summary>
    public Result<Unit> RegisterStream(string name, StreamInboundDelegate handler, Action<StreamProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new StreamProcedureBuilder(handler);
        configure?.Invoke(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a server-streaming procedure using a builder configuration.
    /// </summary>
    public Result<Unit> RegisterStream(string name, Action<StreamProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new StreamProcedureBuilder();
        configure(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a client-streaming procedure with an optional per-procedure configuration.
    /// </summary>
    public Result<Unit> RegisterClientStream(string name, ClientStreamInboundDelegate handler, Action<ClientStreamProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new ClientStreamProcedureBuilder(handler);
        configure?.Invoke(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a client-streaming procedure using a builder configuration.
    /// </summary>
    public Result<Unit> RegisterClientStream(string name, Action<ClientStreamProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ClientStreamProcedureBuilder();
        configure(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a duplex-streaming procedure with an optional per-procedure configuration.
    /// </summary>
    public Result<Unit> RegisterDuplex(string name, DuplexInboundDelegate handler, Action<DuplexProcedureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = new DuplexProcedureBuilder(handler);
        configure?.Invoke(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>
    /// Registers a duplex-streaming procedure using a builder configuration.
    /// </summary>
    public Result<Unit> RegisterDuplex(string name, Action<DuplexProcedureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new DuplexProcedureBuilder();
        configure(builder);
        return RegisterProcedure(name, trimmed => builder.Build(ServiceName, trimmed));
    }

    /// <summary>Attempts to get a procedure by name and kind.</summary>
    public bool TryGetProcedure(string name, ProcedureKind kind, out ProcedureSpec spec) =>
        _procedures.TryGet(ServiceName, name, kind, out spec);

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
        var transport = request.Meta.Transport ?? "unknown";

        return ResolveProcedure<UnaryProcedureSpec>(
                procedure,
                ProcedureKind.Unary,
                transport)
            .Map(spec => MiddlewareComposer.ComposeUnaryInbound(
                CombineMiddleware(_inboundUnaryMiddleware, spec.Middleware),
                spec.Handler))
            .ThenValueTaskAsync((pipeline, token) => pipeline(request, token), cancellationToken);
    }

    /// <summary>
    /// Invokes a registered oneway procedure through the inbound middleware pipeline.
    /// </summary>
    public ValueTask<Result<OnewayAck>> InvokeOnewayAsync(
        string procedure,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        var transport = request.Meta.Transport ?? "unknown";

        return ResolveProcedure<OnewayProcedureSpec>(
                procedure,
                ProcedureKind.Oneway,
                transport)
            .Map(spec => MiddlewareComposer.ComposeOnewayInbound(
                CombineMiddleware(_inboundOnewayMiddleware, spec.Middleware),
                spec.Handler))
            .ThenValueTaskAsync((pipeline, token) => pipeline(request, token), cancellationToken);
    }

    /// <summary>
    /// Returns the client configuration and outbound bindings for a remote service.
    /// </summary>
    public Result<ClientConfiguration> ClientConfig(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return Err<ClientConfiguration>(CreateDispatcherError(
                "Service identifier cannot be null or whitespace.",
                OmniRelayStatusCode.InvalidArgument));
        }

        if (!_outbounds.TryGetValue(service, out var collection))
        {
            if (string.Equals(service, ServiceName, StringComparison.OrdinalIgnoreCase))
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
                return Err<ClientConfiguration>(CreateDispatcherError(
                    $"No outbound configuration found for service '{service}'.",
                    OmniRelayStatusCode.NotFound));
            }
        }

        return Ok(new ClientConfiguration(
            collection,
            _outboundUnaryMiddleware,
            _outboundOnewayMiddleware,
            _outboundStreamMiddleware,
            _outboundClientStreamMiddleware,
            _outboundDuplexMiddleware));
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

        var transport = request.Meta.Transport ?? "stream";

        return ResolveProcedure<StreamProcedureSpec>(
                procedure,
                ProcedureKind.Stream,
                transport,
                $"Stream procedure '{procedure}' is not registered for service '{ServiceName}'.")
            .Map(spec => MiddlewareComposer.ComposeStreamInbound(
                CombineMiddleware(_inboundStreamMiddleware, spec.Middleware),
                spec.Handler))
            .ThenValueTaskAsync((pipeline, token) => pipeline(request, options, token), cancellationToken);
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

        var transport = request.Meta.Transport ?? "stream";

        return ResolveProcedure<DuplexProcedureSpec>(
                procedure,
                ProcedureKind.Duplex,
                transport,
                $"Duplex stream procedure '{procedure}' is not registered for service '{ServiceName}'.")
            .Map(spec => MiddlewareComposer.ComposeDuplexInbound(
                CombineMiddleware(_inboundDuplexMiddleware, spec.Middleware),
                spec.Handler))
            .ThenValueTaskAsync((pipeline, token) => pipeline(request, token), cancellationToken);
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

        var transport = requestMeta.Transport ?? "unknown";

        var result = ResolveProcedure<ClientStreamProcedureSpec>(
                procedure,
                ProcedureKind.ClientStream,
                transport,
                $"Client stream procedure '{procedure}' is not registered for service '{ServiceName}'.")
            .Map(spec =>
            {
                var call = ClientStreamCall.Create(requestMeta);
                var context = new ClientStreamRequestContext(call.RequestMeta, call.Reader);
                var middleware = CombineMiddleware(_inboundClientStreamMiddleware, spec.Middleware);
                var pipeline = MiddlewareComposer.ComposeClientStreamInbound(middleware, spec.Handler);

                _ = ProcessClientStreamAsync(call, context, pipeline, cancellationToken);
                return call;
            });

        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Starts all dispatcher lifecycle components in parallel.
    /// </summary>
    public async ValueTask<Result<Unit>> StartAsync(CancellationToken cancellationToken = default)
    {
        var begin = TryBeginStart();
        if (begin.IsFailure)
        {
            return begin;
        }

        try
        {
            if (_lifecycleStartOrder.Length == 0)
            {
                CompleteStart();
                return Ok(Unit.Value);
            }

            var startedComponents = new int[_lifecycleStartOrder.Length];
            var errors = new ConcurrentQueue<Error>();

            using var group = new ErrGroup(cancellationToken);

            foreach (var (component, index) in _lifecycleStartOrder.Select((component, index) => (component, index)))
            {
                var lifecycle = component.Lifecycle;

                group.Go(async token =>
                {
                    var startResult = await ExecuteLifecycleWithPolicyAsync(
                        component.Name,
                        lifecycle.StartAsync,
                        _startRetryPolicy,
                        token).ConfigureAwait(false);

                    if (startResult.IsSuccess)
                    {
                        Volatile.Write(ref startedComponents[index], 1);
                        return startResult;
                    }

                    if (startResult.Error is not null)
                    {
                        errors.Enqueue(startResult.Error);
                    }

                    return startResult;
                });
            }

            var waitResult = await group.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (!errors.IsEmpty || waitResult.IsFailure)
            {
                lock (_stateLock)
                {
                    _status = DispatcherStatus.Stopped;
                }

                var startError = CreateStartFailureError(errors, group.Error ?? waitResult.Error);

                var rollbackResult = await RollbackStartedComponentsAsync(startedComponents).ConfigureAwait(false);
                if (rollbackResult.IsFailure)
                {
                    if (startError is not null)
                    {
                        var aggregate = AggregateErrors(
                            "Dispatcher start failed and rollback reported additional errors.",
                            [startError, rollbackResult.Error!]);
                        return Err<Unit>(aggregate);
                    }

                    return rollbackResult;
                }

                if (startError is not null)
                {
                    return Err<Unit>(startError);
                }

                return Err<Unit>(CreateDispatcherError("One or more dispatcher components failed to start.", OmniRelayStatusCode.Internal));
            }

            CompleteStart();
            return Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _status = DispatcherStatus.Stopped;
            }

            return OmniRelayErrors.ToResult<Unit>(ex, "dispatcher");
        }
    }

    /// <summary>
    /// Stops all dispatcher lifecycle components in reverse start order.
    /// </summary>
    public async ValueTask<Result<Unit>> StopAsync(CancellationToken cancellationToken = default)
    {
        var stopDecision = TryBeginStop();
        if (stopDecision.IsFailure)
        {
            return Err<Unit>(stopDecision.Error!);
        }

        if (!stopDecision.Value)
        {
            return Ok(Unit.Value);
        }

        var errors = new ConcurrentQueue<Error>();
        var waitGroup = new WaitGroup();

        for (var index = _lifecycleStartOrder.Length - 1; index >= 0; index--)
        {
            var component = _lifecycleStartOrder[index];
            var lifecycle = component.Lifecycle;
            waitGroup.Go(async token =>
            {
                var stopResult = await ExecuteLifecycleWithPolicyAsync(
                    component.Name,
                    lifecycle.StopAsync,
                    _stopRetryPolicy,
                    token).ConfigureAwait(false);

                if (stopResult.IsFailure && stopResult.Error is not null)
                {
                    errors.Enqueue(stopResult.Error);
                }
            }, cancellationToken);
        }

        try
        {
            await waitGroup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Fast shutdown requested; proceed with whatever completed.
        }

        if (!errors.IsEmpty)
        {
            FailStop();
            var errorArray = errors.ToArray();
            if (errorArray.Length == 1)
            {
                return Err<Unit>(errorArray[0]);
            }

            return Err<Unit>(AggregateErrors("One or more dispatcher components failed to stop.", errorArray));
        }

        CompleteStop();
        return Ok(Unit.Value);
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
            ServiceName,
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

    private Result<TProcedure> ResolveProcedure<TProcedure>(
        string procedure,
        ProcedureKind kind,
        string transport,
        string? missingMessage = null)
        where TProcedure : ProcedureSpec
    {
        if (_procedures.TryGet(ServiceName, procedure, kind, out var spec) &&
            spec is TProcedure typed)
        {
            return Ok(typed);
        }

        var message = missingMessage ?? $"{kind} procedure '{procedure}' is not registered for service '{ServiceName}'.";
        var error = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.Unimplemented,
            message,
            transport: transport);

        return Err<TProcedure>(error);
    }

    private static ValueTask<Result<Unit>> ExecuteLifecycleWithPolicyAsync(
        string componentName,
        Func<CancellationToken, ValueTask> lifecycleAction,
        ResultExecutionPolicy policy,
        CancellationToken cancellationToken)
    {
        return Result.RetryWithPolicyAsync<Unit>(
            async (_, token) =>
            {
                try
                {
                    await lifecycleAction(token).ConfigureAwait(false);
                    return Ok(Unit.Value);
                }
                catch (Exception ex)
                {
                    return OmniRelayErrors.ToResult<Unit>(ex, componentName);
                }
            },
            policy,
            TimeProvider.System,
            cancellationToken);
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

    private Result<Unit> TryBeginStart()
    {
        lock (_stateLock)
        {
            return _status switch
            {
                DispatcherStatus.Created or DispatcherStatus.Stopped => SetStatus(DispatcherStatus.Starting),
                DispatcherStatus.Starting or DispatcherStatus.Running => Err<Unit>(CreateDispatcherError("Dispatcher is already running or starting.", OmniRelayStatusCode.FailedPrecondition)),
                DispatcherStatus.Stopping => Err<Unit>(CreateDispatcherError("Dispatcher is stopping.", OmniRelayStatusCode.FailedPrecondition)),
                _ => Err<Unit>(CreateDispatcherError($"Dispatcher cannot start from state {_status}.", OmniRelayStatusCode.FailedPrecondition))
            };

            Result<Unit> SetStatus(DispatcherStatus newStatus)
            {
                _status = newStatus;
                return Ok(Unit.Value);
            }
        }
    }

    private Result<bool> TryBeginStop()
    {
        lock (_stateLock)
        {
            return _status switch
            {
                DispatcherStatus.Created or DispatcherStatus.Stopped => ReturnAndSet(DispatcherStatus.Stopped, false),
                DispatcherStatus.Starting => Err<bool>(CreateDispatcherError("Dispatcher is starting. Await StartAsync completion before stopping.", OmniRelayStatusCode.FailedPrecondition)),
                DispatcherStatus.Stopping => Ok(false),
                _ => SetStopping()
            };

            Result<bool> ReturnAndSet(DispatcherStatus newStatus, bool value)
            {
                _status = newStatus;
                return Ok(value);
            }

            Result<bool> SetStopping()
            {
                _status = DispatcherStatus.Stopping;
                return Ok(true);
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

    private static Error CreateDispatcherError(string message, OmniRelayStatusCode status = OmniRelayStatusCode.Internal) =>
        OmniRelayErrorAdapter.FromStatus(status, message, transport: "dispatcher");

    private static Error AggregateErrors(string message, IEnumerable<Error> errors)
    {
        var aggregate = new AggregateException(message, errors.Select(static error => new ResultException(error)));
        return OmniRelayErrors.ToResult<Unit>(aggregate, "dispatcher").Error!;
    }

    private static Error? CreateStartFailureError(ConcurrentQueue<Error> errors, Error? groupError)
    {
        if (!errors.IsEmpty)
        {
            var snapshots = errors.ToArray();
            return snapshots.Length == 1
                ? snapshots[0]
                : AggregateErrors("One or more dispatcher components failed to start.", snapshots);
        }

        return groupError;
    }

    private async Task<Result<Unit>> RollbackStartedComponentsAsync(int[] startedComponents)
    {
        if (_lifecycleStartOrder.Length == 0 || startedComponents.Length == 0)
        {
            return Ok(Unit.Value);
        }

        var rollbackErrors = new List<Error>();

        for (var index = _lifecycleStartOrder.Length - 1; index >= 0; index--)
        {
            if (Volatile.Read(ref startedComponents[index]) == 0)
            {
                continue;
            }

            var component = _lifecycleStartOrder[index];
            var lifecycle = component.Lifecycle;

            var rollbackResult = await ExecuteLifecycleWithPolicyAsync(
                component.Name,
                lifecycle.StopAsync,
                _stopRetryPolicy,
                CancellationToken.None).ConfigureAwait(false);

            if (rollbackResult.IsFailure && rollbackResult.Error is not null)
            {
                rollbackErrors.Add(rollbackResult.Error);
            }
        }

        if (rollbackErrors.Count == 1)
        {
            return Err<Unit>(rollbackErrors[0]);
        }

        if (rollbackErrors.Count > 1)
        {
            return Err<Unit>(AggregateErrors("One or more dispatcher components failed to roll back after a start failure.", rollbackErrors));
        }

        return Ok(Unit.Value);
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

    private Result<Unit> RegisterProcedure(string name, Func<string, ProcedureSpec> builder)
    {
        return EnsureProcedureName(name)
            .Then(trimmed => BuildProcedureSpec(() => builder(trimmed)))
            .Then(Register);
    }

    private static Result<ProcedureSpec> BuildProcedureSpec(Func<ProcedureSpec> build)
    {
        try
        {
            return Ok(build());
        }
        catch (InvalidOperationException ex)
        {
            return Err<ProcedureSpec>(CreateDispatcherError(ex.Message, OmniRelayStatusCode.InvalidArgument));
        }
    }

    private static Result<string> EnsureProcedureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Err<string>(CreateDispatcherError(
                "Procedure name cannot be null or whitespace.",
                OmniRelayStatusCode.InvalidArgument));
        }

        return Ok(name.Trim());
    }
}
