using Hugo;
using Microsoft.Extensions.Options;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Peers;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal sealed class MeshDispatcherHostedService(
    IOptions<MeshDemoOptions> options,
    PeerLeaseHealthTracker leaseHealthTracker,
    IResourceLeaseReplicator replicator,
    SqliteDeterministicStateStore deterministicStateStore,
    IEnumerable<IResourceLeaseBackpressureListener> backpressureListeners,
    ILogger<MeshDispatcherHostedService> logger,
    IMeshGossipAgent? gossipAgent = null)
    : IHostedService, IAsyncDisposable
{
    private readonly MeshDemoOptions _options = options.Value;
    private readonly PeerLeaseHealthTracker _leaseHealthTracker = leaseHealthTracker;
    private readonly IResourceLeaseReplicator _replicator = replicator;
    private readonly SqliteDeterministicStateStore _deterministicStateStore = deterministicStateStore;
    private readonly IEnumerable<IResourceLeaseBackpressureListener> _backpressureListeners = backpressureListeners;
    private readonly IMeshGossipAgent? _gossipAgent = gossipAgent;
    private readonly ILogger<MeshDispatcherHostedService> _logger = logger;
    private OmniRelayDispatcher? _dispatcher;
    private ResourceLeaseDispatcherComponent? _component;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dispatcherOptions = new DispatcherOptions(_options.ServiceName);
        dispatcherOptions.AddLifecycle("mesh-http-inbound", new HttpInbound([_options.RpcUrl]));

        if (_gossipAgent?.IsEnabled == true)
        {
            dispatcherOptions.AddLifecycle("mesh-gossip", _gossipAgent);
        }
        _dispatcher = new OmniRelayDispatcher(dispatcherOptions);

        var queueOptions = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            Backpressure = new TaskQueueBackpressureOptions
            {
                HighWatermark = 64,
                LowWatermark = 16,
                Cooldown = TimeSpan.FromSeconds(5)
            }
        };

        _component = new ResourceLeaseDispatcherComponent(
            _dispatcher,
            new ResourceLeaseDispatcherOptions
            {
                Namespace = _options.Namespace,
                QueueOptions = queueOptions,
                LeaseHealthTracker = _leaseHealthTracker,
                Replicator = _replicator,
                DeterministicOptions = new ResourceLeaseDeterministicOptions
                {
                    StateStore = _deterministicStateStore,
                    ChangeId = $"{_options.ServiceName}.resourcelease",
                    MinVersion = 1,
                    MaxVersion = 1
                },
                BackpressureListener = ComposeBackpressureListener(_backpressureListeners)
            });

        await _dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ResourceLease dispatcher '{Service}' listening on {Url}", _options.ServiceName, _options.RpcUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_component is not null)
        {
            await _component.DisposeAsync().ConfigureAwait(false);
        }

        if (_dispatcher is not null)
        {
            await _dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_component is not null)
        {
            await _component.DisposeAsync().ConfigureAwait(false);
        }

        if (_dispatcher is not null)
        {
            await _dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_replicator is IAsyncDisposable asyncReplicator)
        {
            await asyncReplicator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IResourceLeaseBackpressureListener? ComposeBackpressureListener(IEnumerable<IResourceLeaseBackpressureListener> listeners)
    {
        var array = listeners.Where(l => l is not null).ToArray();
        if (array.Length == 0)
        {
            return null;
        }

        if (array.Length == 1)
        {
            return array[0];
        }

        return new CompositeBackpressureListener(array);
    }

    private sealed class CompositeBackpressureListener(IReadOnlyList<IResourceLeaseBackpressureListener> listeners)
        : IResourceLeaseBackpressureListener
    {
        private readonly IReadOnlyList<IResourceLeaseBackpressureListener> _listeners = listeners;

        public async ValueTask OnBackpressureChanged(ResourceLeaseBackpressureSignal signal, CancellationToken cancellationToken)
        {
            foreach (var listener in _listeners)
            {
                await listener.OnBackpressureChanged(signal, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
