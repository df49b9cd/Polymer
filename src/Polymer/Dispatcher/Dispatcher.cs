using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using static Hugo.Go;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;

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
    private readonly ImmutableArray<IUnaryOutboundMiddleware> _outboundUnaryMiddleware;
    private readonly ImmutableArray<IOnewayOutboundMiddleware> _outboundOnewayMiddleware;
    private readonly ImmutableArray<IStreamOutboundMiddleware> _outboundStreamMiddleware;
    private readonly object _stateLock = new();
    private DispatcherStatus _status = DispatcherStatus.Created;

    public Dispatcher(DispatcherOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _serviceName = options.ServiceName;
        _lifecycleDescriptors = options.ComponentDescriptors.ToImmutableArray();
        _lifecycleStartOrder = options.UniqueComponents.ToImmutableArray();
        _outbounds = BuildOutboundCollections(options.OutboundBuilders);

        _inboundUnaryMiddleware = options.UnaryInboundMiddleware.ToImmutableArray();
        _inboundOnewayMiddleware = options.OnewayInboundMiddleware.ToImmutableArray();
        _inboundStreamMiddleware = options.StreamInboundMiddleware.ToImmutableArray();
        _outboundUnaryMiddleware = options.UnaryOutboundMiddleware.ToImmutableArray();
        _outboundOnewayMiddleware = options.OnewayOutboundMiddleware.ToImmutableArray();
        _outboundStreamMiddleware = options.StreamOutboundMiddleware.ToImmutableArray();
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
    public IReadOnlyList<IUnaryOutboundMiddleware> UnaryOutboundMiddleware => _outboundUnaryMiddleware;
    public IReadOnlyList<IOnewayOutboundMiddleware> OnewayOutboundMiddleware => _outboundOnewayMiddleware;
    public IReadOnlyList<IStreamOutboundMiddleware> StreamOutboundMiddleware => _outboundStreamMiddleware;

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
        _procedures.TryGet(name, kind, out spec);

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
            _outboundStreamMiddleware);
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
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(spec => new ProcedureDescriptor(spec.Name, spec.Kind, spec.Encoding))
            .ToImmutableArray();

        var components = _lifecycleDescriptors
            .Select(component =>
                new LifecycleComponentDescriptor(
                    component.Name,
                    component.Lifecycle.GetType().FullName ?? component.Lifecycle.GetType().Name))
            .ToImmutableArray();

        var outbounds = _outbounds.Values
            .OrderBy(collection => collection.Service, StringComparer.OrdinalIgnoreCase)
            .Select(collection =>
                new OutboundSummary(
                    collection.Service,
                    collection.Unary.Count,
                    collection.Oneway.Count,
                    collection.Stream.Count))
            .ToImmutableArray();

        var middleware = new MiddlewareSummary(
            _inboundUnaryMiddleware.Select(m => m.GetType().FullName ?? m.GetType().Name).ToImmutableArray(),
            _inboundOnewayMiddleware.Select(m => m.GetType().FullName ?? m.GetType().Name).ToImmutableArray(),
            _inboundStreamMiddleware.Select(m => m.GetType().FullName ?? m.GetType().Name).ToImmutableArray(),
            _outboundUnaryMiddleware.Select(m => m.GetType().FullName ?? m.GetType().Name).ToImmutableArray(),
            _outboundOnewayMiddleware.Select(m => m.GetType().FullName ?? m.GetType().Name).ToImmutableArray(),
            _outboundStreamMiddleware.Select(m => m.GetType().FullName ?? m.GetType().Name).ToImmutableArray());

        return new DispatcherIntrospection(
            _serviceName,
            Status,
            procedures,
            components,
            outbounds,
            middleware);
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
            return ImmutableDictionary<string, OutboundCollection>.Empty;
        }

        var map = ImmutableDictionary.CreateBuilder<string, OutboundCollection>(StringComparer.OrdinalIgnoreCase);

        foreach (var (service, builder) in builders)
        {
            map[service] = builder.Build();
        }

        return map.ToImmutable();
    }
}
