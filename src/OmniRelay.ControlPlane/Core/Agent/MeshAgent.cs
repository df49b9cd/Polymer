using Hugo;
using Microsoft.Extensions.Options;
using OmniRelay.Core.Transport;
using OmniRelay.Protos.Control;
using OmniRelay.Identity;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Minimal MeshKit agent: watches control-plane, applies LKG caching, emits telemetry.</summary>
public sealed class MeshAgent : ILifecycle, IDisposable
{
    private readonly WatchHarness _harness;
    private readonly MeshAgentOptions _options;
    private readonly Microsoft.Extensions.Logging.ILogger<MeshAgent> _logger;
    private CancellationTokenSource? _cts;
    private Task? _watchTask;

    public MeshAgent(WatchHarness harness, IOptions<MeshAgentOptions> options, Microsoft.Extensions.Logging.ILogger<MeshAgent> logger)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_watchTask is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nodeId = string.IsNullOrWhiteSpace(_options.ControlDomain)
            ? _options.NodeId
            : $"{_options.ControlDomain}:{_options.NodeId}";

        var request = new ControlWatchRequest
        {
            NodeId = nodeId,
            Capabilities = new CapabilitySet
            {
                BuildEpoch = typeof(MeshAgent).Assembly.GetName().Version?.ToString() ?? "unknown"
            }
        };
        request.Capabilities.Items.AddRange(_options.Capabilities);
        _watchTask = Go.Run(async token =>
        {
            var result = await _harness.RunAsync(request, token).ConfigureAwait(false);
            if (result.IsFailure)
            {
                AgentLog.ControlWatchFailed(_logger, result.Error?.Cause ?? new InvalidOperationException(result.Error?.Message ?? "control watch failed"));
            }
        }, cancellationToken: _cts.Token).AsTask();
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
