using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Protos.Control;
using OmniRelay.Core.Transport;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Minimal MeshKit agent: watches control-plane, applies LKG caching, emits telemetry.</summary>
public sealed class MeshAgent : ILifecycle, IDisposable
{
    private readonly WatchHarness _harness;
    private readonly ILogger<MeshAgent> _logger;
    private CancellationTokenSource? _cts;
    private Task? _watchTask;

    public MeshAgent(WatchHarness harness, ILogger<MeshAgent> logger)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_watchTask is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var request = new ControlWatchRequest { NodeId = Environment.MachineName };
        _watchTask = Task.Run(() => _harness.RunAsync(request, _cts.Token), _cts.Token);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        if (_watchTask is not null)
        {
            try
            {
                await _watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _watchTask = null;
        _cts.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

}
