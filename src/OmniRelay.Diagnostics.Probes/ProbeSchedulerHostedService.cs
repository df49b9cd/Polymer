using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Diagnostics;

internal sealed class ProbeSchedulerHostedService : BackgroundService
{
    private readonly IEnumerable<IHealthProbe> _probes;
    private readonly ILogger<ProbeSchedulerHostedService> _logger;
    private readonly ProbeSnapshotStore _snapshotStore;

    public ProbeSchedulerHostedService(
        IEnumerable<IHealthProbe> probes,
        ProbeSnapshotStore snapshotStore,
        ILogger<ProbeSchedulerHostedService> logger)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var probe in _probes)
        {
            _ = RunProbeLoopAsync(probe, stoppingToken);
        }

        return Task.CompletedTask;
    }

    private async Task RunProbeLoopAsync(IHealthProbe probe, CancellationToken cancellationToken)
    {
        if (probe.Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Probe {probe.Name} must specify an interval greater than zero.");
        }

        using var timer = new PeriodicTimer(probe.Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                var result = await probe.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = new ProbeExecutionSnapshot(
                    probe.Name,
                    started,
                    result.Succeeded,
                    result.Duration,
                    result.Error,
                    result.Metadata);
                _snapshotStore.Record(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Probe {Probe} failed.", probe.Name);
                var snapshot = new ProbeExecutionSnapshot(
                    probe.Name,
                    started,
                    false,
                    DateTimeOffset.UtcNow - started,
                    ex.Message,
                    null);
                _snapshotStore.Record(snapshot);
            }
        }
    }
}
