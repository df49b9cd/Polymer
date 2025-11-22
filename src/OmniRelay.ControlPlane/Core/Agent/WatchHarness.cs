using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Protos.Control;
using System.Diagnostics;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>
/// Shared bootstrap/watch harness: load LKG, validate, apply, and resume watches with backoff.
/// </summary>
public sealed class WatchHarness
{
    private readonly IControlPlaneWatchClient _client;
    private readonly IControlPlaneConfigValidator _validator;
    private readonly IControlPlaneConfigApplier _applier;
    private readonly LkgCache _cache;
    private readonly TelemetryForwarder _telemetry;
    private readonly ILogger<WatchHarness> _logger;
    private readonly TimeSpan _backoffStart = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _backoffMax = TimeSpan.FromSeconds(30);

    public WatchHarness(
        IControlPlaneWatchClient client,
        IControlPlaneConfigValidator validator,
        IControlPlaneConfigApplier applier,
        LkgCache cache,
        TelemetryForwarder telemetry,
        ILogger<WatchHarness> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(ControlWatchRequest request, CancellationToken cancellationToken)
    {
        // LKG bootstrap
        if (_cache.TryLoad(out var version, out var payload))
        {
            if (TryValidate(payload, out _))
            {
                await _applier.ApplyAsync(version, payload, cancellationToken).ConfigureAwait(false);
                _telemetry.RecordSnapshot(version);
                AgentLog.LkgApplied(_logger, version);
            }
        }

        var backoff = _backoffStart;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var update in _client.WatchAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    backoff = _backoffStart; // reset on success
                    if (!TryValidate(update.Payload.ToByteArray(), out var err))
                    {
                        AgentLog.ControlUpdateRejected(_logger, update.Version, err ?? "unknown");
                        continue;
                    }

                    var payloadBytes = update.Payload.ToByteArray();
                    await _applier.ApplyAsync(update.Version, payloadBytes, cancellationToken).ConfigureAwait(false);
                    _cache.Save(update.Version, payloadBytes);
                    _telemetry.RecordSnapshot(update.Version);
                    AgentLog.ControlUpdateApplied(_logger, update.Version);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AgentLog.ControlWatchFailed(_logger, ex);
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromMilliseconds(Math.Min(_backoffMax.TotalMilliseconds, backoff.TotalMilliseconds * 2));
            }
        }
    }

    private bool TryValidate(byte[] payload, out string? error)
    {
        var sw = Stopwatch.StartNew();
        var ok = _validator.Validate(payload, out error);
        AgentLog.ControlValidationResult(_logger, ok, sw.Elapsed.TotalMilliseconds);
        return ok;
    }
}
