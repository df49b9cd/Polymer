using System.Threading.Channels;
using Hugo;
using Hugo.Policies;
using Microsoft.Extensions.Logging;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Placeholder telemetry forwarder; plug into OTLP/exporters later.</summary>
public sealed partial class TelemetryForwarder : IAsyncDisposable
{
    private readonly ILogger<TelemetryForwarder> _logger;
    private readonly Channel<string> _snapshots;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private readonly TelemetryForwarderOptions _options;
    private readonly TimeProvider _timeProvider;

    public TelemetryForwarder(
        ILogger<TelemetryForwarder> logger,
        TelemetryForwarderOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new TelemetryForwarderOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        _snapshots = MakeChannel<string>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _pumpTask = RunPumpAsync(_cts.Token);
    }

    public void RecordSnapshot(string version)
    {
        _ = _snapshots.Writer.TryWrite(version);
        AgentLog.SnapshotApplied(_logger, version);
    }

    private async Task RunPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var windowResult = await Result.RetryWithPolicyAsync(
                async (ctx, ct) =>
                {
                    var reader = await ResultPipelineChannels.WindowAsync(
                        ctx,
                        _snapshots.Reader,
                        _options.BatchSize,
                        _options.FlushInterval,
                        ct).ConfigureAwait(false);

                    return Result.Ok(reader);
                },
                ResultExecutionPolicy.None,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);

            if (windowResult.IsFailure)
            {
                TelemetryForwarderLog.PumpFailed(_logger, new InvalidOperationException(windowResult.Error!.ToString()));
                return;
            }

            var windowed = windowResult.Value;

            await foreach (var batch in windowed.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (batch.Count == 0)
                {
                    continue;
                }

                try
                {
                    if (_options.OnBatch is not null)
                    {
                        await _options.OnBatch(batch, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        TelemetryForwarderLog.BatchForwarded(_logger, batch.Count, batch[^1]);
                    }
                }
                catch (Exception ex)
                {
                    TelemetryForwarderLog.BatchForwardFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            TelemetryForwarderLog.PumpFailed(_logger, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _snapshots.Writer.TryComplete();

        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    public sealed record TelemetryForwarderOptions(
        int BatchSize = 20,
        TimeSpan FlushInterval = default,
        int ChannelCapacity = 256,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask>? OnBatch = null)
    {
        public TimeSpan FlushInterval { get; init; } = FlushInterval == default ? TimeSpan.FromSeconds(2) : FlushInterval;
    }

    private static partial class TelemetryForwarderLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Forwarding {Count} telemetry snapshots (latest={Latest}).")]
        public static partial void BatchForwarded(ILogger logger, int count, string latest);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Telemetry forwarder batch failed.")]
        public static partial void BatchForwardFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Telemetry forwarder pump failed.")]
        public static partial void PumpFailed(ILogger logger, Exception exception);
    }
}
