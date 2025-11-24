using OmniRelay.Core.Transport;
using OmniRelay.Protos.Control;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Minimal MeshKit agent: watches control-plane, applies LKG caching, emits telemetry.</summary>
public sealed class MeshAgent : ILifecycle, IDisposable
{
    private readonly WatchHarness _harness;
    private readonly Microsoft.Extensions.Logging.ILogger<MeshAgent> _logger;
    private CancellationTokenSource? _cts;
    private Task? _watchTask;

    public MeshAgent(WatchHarness harness, Microsoft.Extensions.Logging.ILogger<MeshAgent> logger)
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
        var request = new ControlWatchRequest
        {
            NodeId = Environment.MachineName,
            Capabilities = new CapabilitySet
            {
                Items = { "core/v1", "dsl/v1" },
                BuildEpoch = typeof(MeshAgent).Assembly.GetName().Version?.ToString() ?? "unknown"
            }
        };
        _watchTask = Task.Run(async () =>
        {
            var result = await _harness.RunAsync(request, _cts.Token).ConfigureAwait(false);
            if (result.IsFailure)
            {
                AgentLog.ControlWatchFailed(_logger, result.Error?.Cause ?? new InvalidOperationException(result.Error?.Message ?? "control watch failed"));
            }
        }, _cts.Token);
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
