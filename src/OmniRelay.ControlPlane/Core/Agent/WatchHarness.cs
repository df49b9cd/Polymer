using System.Diagnostics;
using System.Runtime.CompilerServices;
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
public sealed class WatchHarness : IAsyncDisposable
{
    private static readonly Error PayloadInvalidError = Error.From("Control payload failed validation.", "control.payload.invalid");

    private readonly IControlPlaneWatchClient _client;
    private readonly IControlPlaneConfigValidator _validator;
    private readonly IControlPlaneConfigApplier _applier;
    private readonly LkgCache _cache;
    private readonly TelemetryForwarder _telemetry;
    private readonly ILogger<WatchHarness> _logger;
    private readonly TimeProvider _timeProvider;
    private const int BaseBackoffMillis = 1_000;
    private const int MaxBackoffMillis = 30_000;
    private long _lastServerBackoffMillis;
    private TaskQueue<Func<CancellationToken, ValueTask<Result<Unit>>>>? _applyQueue;
    private SafeTaskQueueWrapper<Func<CancellationToken, ValueTask<Result<Unit>>>>? _applySafeQueue;
    private TaskQueueChannelAdapter<Func<CancellationToken, ValueTask<Result<Unit>>>>? _applyAdapter;
    private TaskQueueOptions? _applyQueueOptions;
    private Task? _applyPump;

    private byte[]? _resumeToken;

    public WatchHarness(
        IControlPlaneWatchClient client,
        IControlPlaneConfigValidator validator,
        IControlPlaneConfigApplier applier,
        LkgCache cache,
        TelemetryForwarder telemetry,
        ILogger<WatchHarness> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask DisposeAsync()
    {
        if (_applyAdapter is not null)
        {
            await _applyAdapter.DisposeAsync().ConfigureAwait(false);
            _applyAdapter = null;
        }

        if (_applySafeQueue is not null)
        {
            await _applySafeQueue.DisposeAsync().ConfigureAwait(false);
            _applySafeQueue = null;
        }

        _applyQueue = null;
        _applyQueueOptions = null;
    }

    public async ValueTask<Result<Unit>> RunAsync(ControlWatchRequest request, CancellationToken cancellationToken)
    {
        _applyQueueOptions = CreateApplyQueueOptions();
        _applyQueue = new TaskQueue<Func<CancellationToken, ValueTask<Result<Unit>>>>(_applyQueueOptions, TimeProvider.System, (_, _) => ValueTask.CompletedTask);
        _applySafeQueue = new SafeTaskQueueWrapper<Func<CancellationToken, ValueTask<Result<Unit>>>>(_applyQueue, ownsQueue: true);
        _applyAdapter = TaskQueueChannelAdapter<Func<CancellationToken, ValueTask<Result<Unit>>>>.Create(_applyQueue, concurrency: 1, ownsQueue: false);
        _applyPump = RunApplyPumpAsync(CancellationToken.None);

        try
        {
            var bootstrap = await BootstrapFromLkgAsync(cancellationToken).ConfigureAwait(false);
            if (bootstrap.IsFailure)
            {
                return bootstrap.CastFailure<Unit>();
            }

            var attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await RunWatchLoopAsync(request, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    return result;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Ok(Unit.Value);
                }

                var backoff = ComputeBackoff(attempt, Interlocked.Exchange(ref _lastServerBackoffMillis, 0));
                attempt = Math.Min(attempt + 1, 30);
                AgentLog.ControlBackoffApplied(_logger, (long)backoff.TotalMilliseconds);

                try
                {
                    await Task.Delay(backoff, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Ok(Unit.Value);
                }

                if (result.IsFailure)
                {
                    AgentLog.ControlWatchFailed(_logger, result.Error?.Cause ?? new InvalidOperationException(result.Error?.Message ?? "control watch failed"));
                }
            }

            return Ok(Unit.Value);
        }
        finally
        {
            if (_applyAdapter is not null)
            {
                await _applyAdapter.DisposeAsync().ConfigureAwait(false);
                _applyAdapter = null;
            }

            if (_applyPump is not null)
            {
                try
                {
                    await _applyPump.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }

            if (_applySafeQueue is not null)
            {
                await _applySafeQueue.DisposeAsync().ConfigureAwait(false);
                _applySafeQueue = null;
            }

            _applyQueue = null;
            _applyQueueOptions = null;
        }
    }

    private async ValueTask<Result<Unit>> BootstrapFromLkgAsync(CancellationToken cancellationToken)
    {
        var lkgResult = await _cache.TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (lkgResult.IsFailure)
        {
            AgentLog.LkgRejected(_logger, lkgResult.Error?.Code ?? "unknown");
            return Ok(Unit.Value);
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

    private async ValueTask<Result<Unit>> RunWatchLoopAsync(ControlWatchRequest template, CancellationToken cancellationToken)
    {
        try
        {
            var stream = Result.MapStreamAsync(
                _client.WatchAsync(BuildRequest(template), cancellationToken),
                (update, _) =>
                {
                    if (update.Backoff is { Millis: > 0 })
                    {
                        _lastServerBackoffMillis = update.Backoff.Millis;
                    }

                    if (update.Error is not null && !string.IsNullOrWhiteSpace(update.Error.Code))
                    {
                        AgentLog.ControlWatchError(_logger, update.Error.Code, update.Error.Message);
                        var error = Error.From(update.Error.Message ?? "control watch error", update.Error.Code);
                        if (update.Backoff is { Millis: > 0 })
                        {
                            error = error.WithMetadata("backoff.ms", update.Backoff.Millis);
                        }

                        return ValueTask.FromResult<Result<ControlWatchResponse>>(Err<ControlWatchResponse>(error));
                    }

                    AgentLog.ControlWatchResume(_logger, update.ResumeToken?.Version ?? update.Version, update.ResumeToken?.Epoch ?? 0);
                    return ValueTask.FromResult<Result<ControlWatchResponse>>(Ok(update));
                },
                cancellationToken);

            var forEach = await Result.ForEachLinkedCancellationAsync(
                stream,
                async (updateResult, ct) =>
                {
                    if (updateResult.IsFailure)
                    {
                        return updateResult.CastFailure<Unit>();
                    }

                    var work = BuildApplyWork(updateResult.Value);

                    if (_applySafeQueue is not null)
                    {
                        var enqueue = await _applySafeQueue.EnqueueAsync(work, ct).ConfigureAwait(false);
                        return enqueue.IsFailure ? enqueue.CastFailure<Unit>() : Ok(Unit.Value);
                    }

                    return await work(ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            return forEach;
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<Unit>(Error.Canceled("Control watch canceled", cancellationToken));
        }
        catch (Exception ex)
        {
            AgentLog.ControlWatchFailed(_logger, ex);
            return Err<Unit>(Error.FromException(ex));
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

    private Func<CancellationToken, ValueTask<Result<Unit>>> BuildApplyWork(ControlWatchResponse update)
    {
        return async ct =>
        {
            try
            {
                return await ProcessUpdateAsync(update, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == ct)
            {
                return Err<Unit>(Error.Canceled(token: oce.CancellationToken));
            }
            catch (Exception ex)
            {
                AgentLog.ControlWatchFailed(_logger, ex);
                return Err<Unit>(Error.FromException(ex));
            }
        };
    }

    private static TaskQueueOptions CreateApplyQueueOptions() =>
        new()
        {
            Capacity = 64,
            LeaseDuration = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            LeaseSweepInterval = TimeSpan.FromSeconds(10),
            RequeueDelay = TimeSpan.FromMilliseconds(200),
            MaxDeliveryAttempts = 5,
            Name = "control-watch-apply"
        };

    internal static TimeSpan ComputeBackoff(int attempt, long serverBackoffMillis)
    {
        if (serverBackoffMillis > 0)
        {
            var serverHint = TimeSpan.FromMilliseconds(serverBackoffMillis);
            return serverHint <= TimeSpan.FromMilliseconds(MaxBackoffMillis)
                ? serverHint
                : TimeSpan.FromMilliseconds(MaxBackoffMillis);
        }

        var exponent = Math.Pow(2, Math.Clamp(attempt, 0, 30));
        var delayMs = BaseBackoffMillis * exponent;
        if (delayMs > MaxBackoffMillis)
        {
            delayMs = MaxBackoffMillis;
        }

        return TimeSpan.FromMilliseconds(delayMs);
    }

    private async Task RunApplyPumpAsync(CancellationToken cancellationToken)
    {
        if (_applyAdapter is null || _applySafeQueue is null || _applyQueueOptions is null)
        {
            return;
        }

        var maxAttempts = _applyQueueOptions.MaxDeliveryAttempts;

        var aggregated = await Result.CollectErrorsAsync(
            ProcessApplyLeasesAsync(maxAttempts, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (aggregated.IsFailure)
        {
            AgentLog.ControlWatchFailed(_logger, aggregated.Error?.Cause ?? new InvalidOperationException(aggregated.Error?.Message ?? "control apply aggregated failure"));
        }
    }

    private async IAsyncEnumerable<Result<Unit>> ProcessApplyLeasesAsync(
        int maxAttempts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_applyAdapter is null || _applySafeQueue is null)
        {
            yield break;
        }

        await foreach (var lease in _applyAdapter.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var safeLease = _applySafeQueue.Wrap(lease);
            Result<Unit> result;
            try
            {
                result = await lease.Value(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken || cancellationToken.IsCancellationRequested)
            {
                result = Err<Unit>(Error.Canceled(token: oce.CancellationToken));
            }
            catch (Exception ex)
            {
                result = Err<Unit>(Error.FromException(ex));
            }

            if (result.IsSuccess)
            {
                var complete = await safeLease.CompleteAsync(cancellationToken).ConfigureAwait(false);
                if (complete.IsFailure)
                {
                    AgentLog.ControlWatchFailed(_logger, complete.Error?.Cause ?? new InvalidOperationException(complete.Error?.Message ?? "control apply completion failed"));
                    yield return complete.CastFailure<Unit>();
                    continue;
                }

                yield return Ok(Unit.Value);
                continue;
            }

            var requeue = lease.Attempt < maxAttempts && result.Error?.Code != ErrorCodes.Canceled;
            var failed = await safeLease.FailAsync(result.Error!, requeue, cancellationToken).ConfigureAwait(false);
            if (failed.IsFailure)
            {
                AgentLog.ControlWatchFailed(_logger, failed.Error?.Cause ?? new InvalidOperationException(failed.Error?.Message ?? "control apply fail handling failed"));
                yield return failed.CastFailure<Unit>();
                continue;
            }

            yield return result;
        }
    }
}
