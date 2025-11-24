using System.Diagnostics;
using Google.Protobuf;
using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Protos.Control;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>
/// Shared bootstrap/watch harness: load LKG, validate, apply, and resume watches with backoff.
/// </summary>
public sealed class WatchHarness
{
    private static readonly Error PayloadInvalidError = Error.From("Control payload failed validation.", "control.payload.invalid");

    private readonly IControlPlaneWatchClient _client;
    private readonly IControlPlaneConfigValidator _validator;
    private readonly IControlPlaneConfigApplier _applier;
    private readonly LkgCache _cache;
    private readonly TelemetryForwarder _telemetry;
    private readonly ILogger<WatchHarness> _logger;
    private readonly TimeSpan _backoffStart = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _backoffMax = TimeSpan.FromSeconds(30);

    private byte[]? _resumeToken;

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

    public async ValueTask<Result<Unit>> RunAsync(ControlWatchRequest request, CancellationToken cancellationToken)
    {
        var bootstrap = await BootstrapFromLkgAsync(cancellationToken).ConfigureAwait(false);
        if (bootstrap.IsFailure)
        {
            return bootstrap.CastFailure<Unit>();
        }

        var backoff = _backoffStart;

        while (!cancellationToken.IsCancellationRequested)
        {
            var loopResult = await RunWatchLoopAsync(request, backoff, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return Err<Unit>(Error.Canceled("Control watch canceled", cancellationToken));
            }

            backoff = loopResult.IsSuccess ? loopResult.Value : backoff;
            backoff = await ApplyBackoffAsync(backoff, cancellationToken).ConfigureAwait(false);
        }

        return Ok(Unit.Value);
    }

    private async ValueTask<Result<Unit>> BootstrapFromLkgAsync(CancellationToken cancellationToken)
    {
        var lkgResult = await _cache.TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (lkgResult.IsFailure)
        {
            return lkgResult.CastFailure<Unit>();
        }

        if (lkgResult.Value is null)
        {
            return Ok(Unit.Value);
        }

        var snapshot = lkgResult.Value;
        var validation = ValidatePayload(snapshot.Payload);
        if (validation.IsFailure)
        {
            return Ok(Unit.Value); // ignore invalid LKG but continue
        }

        await _applier.ApplyAsync(snapshot.Version, snapshot.Payload, cancellationToken).ConfigureAwait(false);
        _telemetry.RecordSnapshot(snapshot.Version);
        AgentLog.LkgApplied(_logger, snapshot.Version);
        _resumeToken = snapshot.ResumeToken;
        return Ok(Unit.Value);
    }

    private async ValueTask<Result<TimeSpan>> RunWatchLoopAsync(ControlWatchRequest template, TimeSpan currentBackoff, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in _client.WatchAsync(BuildRequest(template), cancellationToken).ConfigureAwait(false))
            {
                if (update.Error is not null && !string.IsNullOrWhiteSpace(update.Error.Code))
                {
                    AgentLog.ControlWatchError(_logger, update.Error.Code, update.Error.Message);
                    var hint = update.Backoff?.Millis is > 0 ? TimeSpan.FromMilliseconds(update.Backoff.Millis) : currentBackoff;
                    return Ok(hint);
                }

                AgentLog.ControlWatchResume(_logger, update.ResumeToken?.Version ?? update.Version, update.ResumeToken?.Epoch ?? 0);

                var applyResult = await ProcessUpdateAsync(update, cancellationToken).ConfigureAwait(false);
                if (applyResult.IsFailure)
                {
                    var hint = update.Backoff?.Millis is > 0 ? TimeSpan.FromMilliseconds(update.Backoff.Millis) : currentBackoff;
                    return Ok(hint);
                }

                currentBackoff = _backoffStart; // reset on success
            }

            return Ok(currentBackoff);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<TimeSpan>(Error.Canceled("Control watch canceled", cancellationToken));
        }
        catch (Exception ex)
        {
            AgentLog.ControlWatchFailed(_logger, ex);
            return Err<TimeSpan>(Error.FromException(ex));
        }
    }

    private ControlWatchRequest BuildRequest(ControlWatchRequest template)
    {
        var request = template.Clone();
        if (_resumeToken is { Length: > 0 })
        {
            request.ResumeToken = WatchResumeToken.Parser.ParseFrom(_resumeToken);
        }

        return request;
    }

    private async Task<TimeSpan> ApplyBackoffAsync(TimeSpan hint, CancellationToken cancellationToken)
    {
        var millis = (long)Math.Max(hint.TotalMilliseconds, _backoffStart.TotalMilliseconds);
        AgentLog.ControlBackoffApplied(_logger, millis);
        var delay = TimeSpan.FromMilliseconds(millis);

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return delay;
        }

        var next = TimeSpan.FromMilliseconds(Math.Min(_backoffMax.TotalMilliseconds, Math.Max(delay.TotalMilliseconds * 2, _backoffStart.TotalMilliseconds)));
        return next;
    }

    private Result<Unit> ValidatePayload(byte[] payload)
    {
        var sw = Stopwatch.StartNew();
        var ok = _validator.Validate(payload, out var error);
        AgentLog.ControlValidationResult(_logger, ok, sw.Elapsed.TotalMilliseconds);

        return ok
            ? Ok(Unit.Value)
            : Err<Unit>(PayloadInvalidError.WithMetadata("reason", error ?? string.Empty));
    }

    private async ValueTask<Result<Unit>> ProcessUpdateAsync(ControlWatchResponse update, CancellationToken cancellationToken)
    {
        var payload = update.Payload.ToByteArray();
        var validation = ValidatePayload(payload);
        if (validation.IsFailure)
        {
            AgentLog.ControlUpdateRejected(_logger, update.Version, validation.Error?.Message ?? "invalid payload");
            return validation;
        }

        var applyResult = await Result.TryAsync<Unit>(async ct =>
        {
            await _applier.ApplyAsync(update.Version, payload, ct).ConfigureAwait(false);
            return Unit.Value;
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (applyResult.IsFailure)
        {
            return applyResult.CastFailure<Unit>();
        }

        var tokenBytes = update.ResumeToken?.ToByteArray() ?? Array.Empty<byte>();
        var persistResult = await _cache.SaveAsync(update.Version, update.Epoch, payload, tokenBytes, cancellationToken).ConfigureAwait(false);
        if (persistResult.IsFailure)
        {
            return persistResult.CastFailure<Unit>();
        }

        _resumeToken = tokenBytes;
        _telemetry.RecordSnapshot(update.Version);
        AgentLog.ControlUpdateApplied(_logger, update.Version);
        return Ok(Unit.Value);
    }
}
