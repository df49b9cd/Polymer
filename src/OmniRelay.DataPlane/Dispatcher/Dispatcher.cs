using System.Collections.Concurrent;
using System.Collections.Immutable;
using Hugo;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Hosting;
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
    private readonly ImmutableDictionary<string, OutboundRegistry> _outbounds;
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
    private readonly OutboundRegistry _loopbackOutbounds;
    private readonly HttpOutboundMiddlewareRegistry? _httpOutboundMiddlewareRegistry;
    private readonly GrpcClientInterceptorRegistry? _grpcClientInterceptorRegistry;
    private readonly GrpcServerInterceptorRegistry? _grpcServerInterceptorRegistry;
    private readonly LifecycleOrchestrator _lifecycleOrchestrator;
    // Cache composed inbound pipelines so per-request dispatch stays allocation-free.
    private readonly ConcurrentDictionary<UnaryProcedureSpec, UnaryInboundHandler> _unaryPipelines = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<OnewayProcedureSpec, OnewayInboundHandler> _onewayPipelines = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<StreamProcedureSpec, StreamInboundHandler> _streamPipelines = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<ClientStreamProcedureSpec, ClientStreamInboundHandler> _clientStreamPipelines = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<DuplexProcedureSpec, DuplexInboundHandler> _duplexPipelines = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Creates a dispatcher for a specific service using the provided options.
    /// </summary>
    public Dispatcher(DispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ServiceName = options.ServiceName;
        _lifecycleDescriptors = [.. options.ComponentDescriptors];
        _outbounds = BuildOutboundRegistrys(options.OutboundBuilders);
        _loopbackOutbounds = new OutboundRegistry(
            ServiceName,
            ImmutableDictionary<string, IUnaryOutbound>.Empty,
            ImmutableDictionary<string, IOnewayOutbound>.Empty,
            ImmutableDictionary<string, IStreamOutbound>.Empty,
            ImmutableDictionary<string, IClientStreamOutbound>.Empty,
            ImmutableDictionary<string, IDuplexOutbound>.Empty);

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
        Codecs = new CodecRegistry(ServiceName, options.CodecRegistrations);
        Mode = options.Mode;
        Capabilities = BuildCapabilities(options.Mode);

        var lifecycleRegistrations = options.UniqueComponents
            .Select(component => new LifecycleComponentRegistration(component.Name, component.Lifecycle, component.Dependencies))
            .ToImmutableArray();
        _lifecycleOrchestrator = new LifecycleOrchestrator(
            lifecycleRegistrations,
            options.StartRetryPolicy,
            options.StopRetryPolicy,
            NullLogger<LifecycleOrchestrator>.Instance);

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

    /// <summary>Gets the deployment mode for this dispatcher host.</summary>
    public DeploymentMode Mode { get; }

    /// <summary>Gets the capability flags advertised by this host.</summary>
    public ImmutableArray<string> Capabilities { get; }

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
    public Result<Unit> RegisterUnary(string name, UnaryInboundHandler handler, Action<UnaryProcedureBuilder>? configure = null)
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
    public Result<Unit> RegisterOneway(string name, OnewayInboundHandler handler, Action<OnewayProcedureBuilder>? configure = null)
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
    public Result<Unit> RegisterStream(string name, StreamInboundHandler handler, Action<StreamProcedureBuilder>? configure = null)
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
    public Result<Unit> RegisterClientStream(string name, ClientStreamInboundHandler handler, Action<ClientStreamProcedureBuilder>? configure = null)
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
    public Result<Unit> RegisterDuplex(string name, DuplexInboundHandler handler, Action<DuplexProcedureBuilder>? configure = null)
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
            .Map(GetUnaryPipeline)
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
            .Map(GetOnewayPipeline)
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
                collection = _loopbackOutbounds;
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
            .Map(GetStreamPipeline)
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
            .Map(GetDuplexPipeline)
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
                var pipeline = GetClientStreamPipeline(spec);

                _ = ProcessClientStreamAsync(call, context, pipeline, cancellationToken);
                return call;
            });

        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Starts all dispatcher lifecycle components using the orchestrator.
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
            var startResult = await _lifecycleOrchestrator.StartAsync(cancellationToken).ConfigureAwait(false);
            if (startResult.IsFailure)
            {
                lock (_stateLock)
                {
                    _status = DispatcherStatus.Stopped;
                }

                // Ensure any components that started before the failure are stopped.
                await _lifecycleOrchestrator.StopAsync(cancellationToken).ConfigureAwait(false);

                return startResult;
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
    /// Stops all dispatcher lifecycle components.
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

        try
        {
            var stopResult = await _lifecycleOrchestrator.StopAsync(cancellationToken).ConfigureAwait(false);
            if (stopResult.IsFailure)
            {
                FailStop();
                return stopResult;
            }

            CompleteStop();
            return Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            FailStop();
            return OmniRelayErrors.ToResult<Unit>(ex, "dispatcher");
        }
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
                    component.Lifecycle.GetType().FullName ?? component.Lifecycle.GetType().Name,
                    component.Dependencies))
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
            middleware,
            Mode,
            Capabilities).Normalize();
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

    private static ImmutableArray<string> BuildCapabilities(DeploymentMode mode)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        builder.Add($"deployment:{mode.ToString().ToLowerInvariant()}");
        builder.Add("feature:http");
        builder.Add("feature:grpc");
        builder.Add("feature:http3:conditional"); // depends on TLS + Kestrel runtime
        builder.Add("feature:aot-safe");
        builder.Add("feature:ext-dsl");
        builder.Add("feature:ext-wasm");
        builder.Add("feature:ext-native");
        return builder.ToImmutable();
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

    private static async Task ProcessClientStreamAsync(
        ClientStreamCall call,
        ClientStreamRequestContext context,
        ClientStreamInboundHandler pipeline,
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

    private static ImmutableDictionary<string, OutboundRegistry> BuildOutboundRegistrys(
        IReadOnlyDictionary<string, DispatcherOptions.OutboundRegistryBuilder> builders)
    {
        if (builders.Count == 0)
        {
            return [];
        }

        var map = ImmutableDictionary.CreateBuilder<string, OutboundRegistry>(StringComparer.OrdinalIgnoreCase);

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

    private UnaryInboundHandler GetUnaryPipeline(UnaryProcedureSpec spec) =>
        _unaryPipelines.GetOrAdd(spec, static (procedure, dispatcher) =>
        {
            var middleware = CombineMiddleware(dispatcher._inboundUnaryMiddleware, procedure.Middleware);
            return MiddlewareComposer.ComposeUnaryInbound(middleware, procedure.Handler);
        }, this);

    private OnewayInboundHandler GetOnewayPipeline(OnewayProcedureSpec spec) =>
        _onewayPipelines.GetOrAdd(spec, static (procedure, dispatcher) =>
        {
            var middleware = CombineMiddleware(dispatcher._inboundOnewayMiddleware, procedure.Middleware);
            return MiddlewareComposer.ComposeOnewayInbound(middleware, procedure.Handler);
        }, this);

    private StreamInboundHandler GetStreamPipeline(StreamProcedureSpec spec) =>
        _streamPipelines.GetOrAdd(spec, static (procedure, dispatcher) =>
        {
            var middleware = CombineMiddleware(dispatcher._inboundStreamMiddleware, procedure.Middleware);
            return MiddlewareComposer.ComposeStreamInbound(middleware, procedure.Handler);
        }, this);

    private ClientStreamInboundHandler GetClientStreamPipeline(ClientStreamProcedureSpec spec) =>
        _clientStreamPipelines.GetOrAdd(spec, static (procedure, dispatcher) =>
        {
            var middleware = CombineMiddleware(dispatcher._inboundClientStreamMiddleware, procedure.Middleware);
            return MiddlewareComposer.ComposeClientStreamInbound(middleware, procedure.Handler);
        }, this);

    private DuplexInboundHandler GetDuplexPipeline(DuplexProcedureSpec spec) =>
        _duplexPipelines.GetOrAdd(spec, static (procedure, dispatcher) =>
        {
            var middleware = CombineMiddleware(dispatcher._inboundDuplexMiddleware, procedure.Middleware);
            return MiddlewareComposer.ComposeDuplexInbound(middleware, procedure.Handler);
        }, this);

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
