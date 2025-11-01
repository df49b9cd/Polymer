using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using static Hugo.Go;
using Polymer.Core;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;
using Polymer.Errors;

namespace Polymer.Dispatcher;

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
    private readonly ImmutableArray<IUnaryOutboundMiddleware> _outboundUnaryMiddleware;
    private readonly ImmutableArray<IOnewayOutboundMiddleware> _outboundOnewayMiddleware;
    private readonly ImmutableArray<IStreamOutboundMiddleware> _outboundStreamMiddleware;
    private readonly ImmutableArray<IClientStreamOutboundMiddleware> _outboundClientStreamMiddleware;
    private readonly object _stateLock = new();
    private DispatcherStatus _status = DispatcherStatus.Created;

    public Dispatcher(DispatcherOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _serviceName = options.ServiceName;
        _lifecycleDescriptors = [.. options.ComponentDescriptors];
        _lifecycleStartOrder = [.. options.UniqueComponents];
        _outbounds = BuildOutboundCollections(options.OutboundBuilders);

        _inboundUnaryMiddleware = [.. options.UnaryInboundMiddleware];
        _inboundOnewayMiddleware = [.. options.OnewayInboundMiddleware];
        _inboundStreamMiddleware = [.. options.StreamInboundMiddleware];
        _inboundClientStreamMiddleware = [.. options.ClientStreamInboundMiddleware];
        _outboundUnaryMiddleware = [.. options.UnaryOutboundMiddleware];
        _outboundOnewayMiddleware = [.. options.OnewayOutboundMiddleware];
        _outboundStreamMiddleware = [.. options.StreamOutboundMiddleware];
        _outboundClientStreamMiddleware = [.. options.ClientStreamOutboundMiddleware];

        BindDispatcherAwareComponents(_lifecycleDescriptors);
    }

    public string ServiceName => _serviceName;

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

    public IReadOnlyList<IUnaryInboundMiddleware> UnaryInboundMiddleware => _inboundUnaryMiddleware;
    public IReadOnlyList<IOnewayInboundMiddleware> OnewayInboundMiddleware => _inboundOnewayMiddleware;
    public IReadOnlyList<IStreamInboundMiddleware> StreamInboundMiddleware => _inboundStreamMiddleware;
    public IReadOnlyList<IClientStreamInboundMiddleware> ClientStreamInboundMiddleware => _inboundClientStreamMiddleware;
    public IReadOnlyList<IUnaryOutboundMiddleware> UnaryOutboundMiddleware => _outboundUnaryMiddleware;
    public IReadOnlyList<IOnewayOutboundMiddleware> OnewayOutboundMiddleware => _outboundOnewayMiddleware;
    public IReadOnlyList<IStreamOutboundMiddleware> StreamOutboundMiddleware => _outboundStreamMiddleware;
    public IReadOnlyList<IClientStreamOutboundMiddleware> ClientStreamOutboundMiddleware => _outboundClientStreamMiddleware;

    public void Register(ProcedureSpec spec)
    {
        if (spec is null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        if (!string.Equals(spec.Service, _serviceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Procedure '{spec.FullName}' does not match dispatcher service '{_serviceName}'.");
        }

        _procedures.Register(spec);
    }

    public bool TryGetProcedure(string name, ProcedureKind kind, out ProcedureSpec spec) =>
        _procedures.TryGet(_serviceName, name, kind, out spec);

    public IReadOnlyCollection<ProcedureSpec> ListProcedures() =>
        _procedures.Snapshot();

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeUnaryAsync(
        string procedure,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.Unary, out var spec) ||
            spec is not UnaryProcedureSpec unarySpec)
        {
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Unimplemented,
                $"Unary procedure '{procedure}' is not registered for service '{_serviceName}'.",
                transport: request.Meta.Transport ?? "unknown");

            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
        }

        var pipeline = MiddlewareComposer.ComposeUnaryInbound(
            CombineMiddleware(_inboundUnaryMiddleware, unarySpec.Middleware),
            unarySpec.Handler);

        return pipeline(request, cancellationToken);
    }

    public ValueTask<Result<OnewayAck>> InvokeOnewayAsync(
        string procedure,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.Oneway, out var spec) ||
            spec is not OnewayProcedureSpec onewaySpec)
        {
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Unimplemented,
                $"Oneway procedure '{procedure}' is not registered for service '{_serviceName}'.",
                transport: request.Meta.Transport ?? "unknown");

        return ValueTask.FromResult(Err<OnewayAck>(error));
        }

        var pipeline = MiddlewareComposer.ComposeOnewayInbound(
            CombineMiddleware(_inboundOnewayMiddleware, onewaySpec.Middleware),
            onewaySpec.Handler);

        return pipeline(request, cancellationToken);
    }

    public ClientConfiguration ClientConfig(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service identifier cannot be null or whitespace.", nameof(service));
        }

        if (!_outbounds.TryGetValue(service, out var collection))
        {
            throw new KeyNotFoundException($"No outbounds registered for service '{service}'.");
        }

        return new ClientConfiguration(
            collection,
            _outboundUnaryMiddleware,
            _outboundOnewayMiddleware,
            _outboundStreamMiddleware,
            _outboundClientStreamMiddleware);
    }

    public ValueTask<Result<IStreamCall>> InvokeStreamAsync(
        string procedure,
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(procedure))
        {
            return ValueTask.FromResult(Err<IStreamCall>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.InvalidArgument,
                "Procedure name is required for streaming calls.")));
        }

        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.Stream, out var spec) ||
            spec is not StreamProcedureSpec streamSpec)
        {
            return ValueTask.FromResult(Err<IStreamCall>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Unimplemented,
                $"Stream procedure '{procedure}' is not registered for service '{_serviceName}'.")));
        }

        var middleware = CombineMiddleware(_inboundStreamMiddleware, streamSpec.Middleware);
        var pipeline = MiddlewareComposer.ComposeStreamInbound(middleware, streamSpec.Handler);
        return pipeline(request, options, cancellationToken);
    }

    public ValueTask<Result<ClientStreamCall>> InvokeClientStreamAsync(
        string procedure,
        RequestMeta requestMeta,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(procedure))
        {
            return ValueTask.FromResult(Err<ClientStreamCall>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.InvalidArgument,
                "Procedure name is required for client streaming calls.")));
        }

        if (requestMeta is null)
        {
            throw new ArgumentNullException(nameof(requestMeta));
        }

        if (!_procedures.TryGet(_serviceName, procedure, ProcedureKind.ClientStream, out var spec) ||
            spec is not ClientStreamProcedureSpec clientStreamSpec)
        {
            return ValueTask.FromResult(Err<ClientStreamCall>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Unimplemented,
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

            var wg = new WaitGroup();

            foreach (var component in _lifecycleStartOrder)
            {
                var lifecycle = component.Lifecycle;
                wg.Go(async token =>
                {
                    await lifecycle.StartAsync(token).ConfigureAwait(false);
                }, cancellationToken);
            }

            await wg.WaitAsync(cancellationToken).ConfigureAwait(false);
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

        CompleteStop();

        if (exceptions.Count == 1)
        {
            throw exceptions[0];
        }

        if (exceptions.Count > 1)
        {
            throw new AggregateException("One or more dispatcher components failed to stop.", exceptions);
        }
    }

    public DispatcherIntrospection Introspect()
    {
        var procedures = _procedures.Snapshot()
            .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static spec => new ProcedureDescriptor(spec.Name, spec.Kind, spec.Encoding))
            .ToImmutableArray();

        var components = _lifecycleDescriptors
            .Select(static component =>
                new LifecycleComponentDescriptor(
                    component.Name,
                    component.Lifecycle.GetType().FullName ?? component.Lifecycle.GetType().Name))
            .ToImmutableArray();

        var outbounds = _outbounds.Values
            .OrderBy(static collection => collection.Service, StringComparer.OrdinalIgnoreCase)
            .Select(static collection =>
                new OutboundSummary(
                    collection.Service,
                    collection.Unary.Count,
                    collection.Oneway.Count,
                    collection.Stream.Count,
                    collection.ClientStream.Count))
            .ToImmutableArray();

        var middleware = new MiddlewareSummary(
            [.. _inboundUnaryMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _inboundOnewayMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _inboundStreamMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _inboundClientStreamMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundUnaryMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundOnewayMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundStreamMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)],
            [.. _outboundClientStreamMiddleware.Select(static m => m.GetType().FullName ?? m.GetType().Name)]);

        return new DispatcherIntrospection(
            _serviceName,
            Status,
            procedures,
            components,
            outbounds,
            middleware);
    }

    private async Task ProcessClientStreamAsync(
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
            var failure = PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, transport);
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
}
