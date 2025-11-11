using System.Text.Json;
using Microsoft.Extensions.Options;
using OmniRelay.Dispatcher;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal sealed class LakehouseCatalogWorkerHostedService : BackgroundService
{
    private readonly ResourceLeaseHttpClient _client;
    private readonly MeshDemoOptions _options;
    private readonly LakehouseCatalogState _catalogState;
    private readonly ILogger<LakehouseCatalogWorkerHostedService> _logger;
    private readonly Random _random = new();

    public LakehouseCatalogWorkerHostedService(
        ResourceLeaseHttpClient client,
        IOptions<MeshDemoOptions> options,
        LakehouseCatalogState catalogState,
        ILogger<LakehouseCatalogWorkerHostedService> logger)
    {
        _client = client;
        _options = options.Value;
        _catalogState = catalogState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ResourceLeaseLeaseResponse? lease;
            try
            {
                lease = await _client.LeaseAsync(_options.WorkerPeerId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lease attempt failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (lease is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            await ProcessLeaseAsync(lease, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessLeaseAsync(ResourceLeaseLeaseResponse lease, CancellationToken cancellationToken)
    {
        LakehouseCatalogOperation? operation;
        try
        {
            operation = JsonSerializer.Deserialize(
                lease.Payload.Body,
                MeshJson.Context.LakehouseCatalogOperation);

            if (operation is null)
            {
                throw new InvalidOperationException("Lease payload did not contain a catalog operation.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize catalog operation for lease {SequenceId}.", lease.SequenceId);
            await _client.FailAsync(lease.OwnershipToken, requeue: false, reason: "invalid catalog payload", cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Applying {Operation} {Catalog}.{Database}.{Table} v{Version} (attempt={Attempt})",
            operation.OperationType,
            operation.Catalog,
            operation.Database,
            operation.Table,
            operation.Version,
            lease.Attempt);

        try
        {
            await SimulateWorkAsync(cancellationToken).ConfigureAwait(false);

            var applyResult = _catalogState.Apply(operation);
            switch (applyResult.Outcome)
            {
                case LakehouseCatalogApplyOutcome.Stale:
                    _logger.LogWarning(
                        "Stale catalog version {Catalog}.{Database}.{Table} v{Version} (current={CurrentVersion}); dropping.",
                        operation.Catalog,
                        operation.Database,
                        operation.Table,
                        operation.Version,
                        applyResult.State.Version);
                    await _client.FailAsync(lease.OwnershipToken, requeue: false, reason: "stale catalog version", cancellationToken).ConfigureAwait(false);
                    return;

                case LakehouseCatalogApplyOutcome.Duplicate:
                    _logger.LogInformation(
                        "Duplicate catalog version {Catalog}.{Database}.{Table} v{Version}; acknowledging.",
                        operation.Catalog,
                        operation.Database,
                        operation.Table,
                        operation.Version);
                    break;

                default:
                    _logger.LogInformation(
                        "Catalog updated {Catalog}.{Database}.{Table} v{Version} ({Operation})",
                        operation.Catalog,
                        operation.Database,
                        operation.Table,
                        operation.Version,
                        operation.OperationType);
                    break;
            }

            await _client.CompleteAsync(lease.OwnershipToken, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Worker crashed applying {Catalog}.{Database}.{Table} v{Version}; requeueing.",
                operation.Catalog,
                operation.Database,
                operation.Table,
                operation.Version);
            await _client.FailAsync(lease.OwnershipToken, requeue: true, reason: "worker exception", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SimulateWorkAsync(CancellationToken cancellationToken)
    {
        var workDuration = TimeSpan.FromMilliseconds(_random.Next(400, 1_250));
        await Task.Delay(workDuration / 2, cancellationToken).ConfigureAwait(false);
        await Task.Delay(workDuration / 2, cancellationToken).ConfigureAwait(false);
    }
}
