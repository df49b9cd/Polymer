using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Hosts a SafeTaskQueue-backed table lease queue and exposes canonical procedures for enqueue, lease, ack, and drain flows.
/// </summary>
public sealed class TableLeaseDispatcherComponent : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TableLeaseDispatcherOptions _options;
    private readonly TaskQueue<TableLeaseWorkItem> _queue;
    private readonly SafeTaskQueueWrapper<TableLeaseWorkItem> _safeQueue;
    private readonly ConcurrentDictionary<TaskQueueOwnershipToken, SafeTaskQueueLease<TableLeaseWorkItem>> _leases = new();
    private readonly ConcurrentDictionary<TaskQueueOwnershipToken, string> _leaseOwners = new();
    private readonly PeerLeaseHealthTracker? _leaseHealthTracker;
    private readonly ITableLeaseReplicator? _replicator;
    private readonly ITableLeaseBackpressureListener? _backpressureListener;
    private readonly ITableLeaseDeterministicCoordinator? _deterministicCoordinator;
    private readonly long? _backpressureHighWatermark;
    private readonly long? _backpressureLowWatermark;
    private readonly TimeSpan? _backpressureCooldown;
    private DateTimeOffset _lastBackpressureTransition;
    private int _isBackpressureActive;
    private TaskCompletionSource<bool>? _backpressureTcs;
    private readonly string _enqueueProcedure;
    private readonly string _leaseProcedure;
    private readonly string _completeProcedure;
    private readonly string _heartbeatProcedure;
    private readonly string _failProcedure;
    private readonly string _drainProcedure;
    private readonly string _restoreProcedure;

    /// <summary>
    /// Creates a table lease component and immediately registers the standard procedures on the supplied dispatcher.
    /// </summary>
    public TableLeaseDispatcherComponent(Dispatcher dispatcher, TableLeaseDispatcherOptions options)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var queueOptions = _options.QueueOptions ?? new TaskQueueOptions();
        _backpressureHighWatermark = queueOptions.Backpressure?.HighWatermark;
        _backpressureLowWatermark = queueOptions.Backpressure?.LowWatermark;
        _backpressureCooldown = queueOptions.Backpressure?.Cooldown;

        var prefix = string.IsNullOrWhiteSpace(_options.Namespace)
            ? "tablelease"
            : _options.Namespace.Trim();

        _enqueueProcedure = $"{prefix}::enqueue";
        _leaseProcedure = $"{prefix}::lease";
        _completeProcedure = $"{prefix}::complete";
        _heartbeatProcedure = $"{prefix}::heartbeat";
        _failProcedure = $"{prefix}::fail";
        _drainProcedure = $"{prefix}::drain";
        _restoreProcedure = $"{prefix}::restore";

        _queue = new TaskQueue<TableLeaseWorkItem>(queueOptions);
        _safeQueue = new SafeTaskQueueWrapper<TableLeaseWorkItem>(_queue, ownsQueue: false);
        _leaseHealthTracker = _options.LeaseHealthTracker;
        _replicator = _options.Replicator;
        _backpressureListener = _options.BackpressureListener;
        _deterministicCoordinator = _options.DeterministicCoordinator ?? (_options.DeterministicOptions is not null
            ? new DeterministicTableLeaseCoordinator(_options.DeterministicOptions!)
            : null);

        RegisterProcedures();
    }

    private void RegisterProcedures()
    {
        _dispatcher.RegisterJsonUnary<TableLeaseEnqueueRequest, TableLeaseEnqueueResponse>(
            _enqueueProcedure,
            HandleEnqueue);

        _dispatcher.RegisterJsonUnary<TableLeaseLeaseRequest, TableLeaseLeaseResponse>(
            _leaseProcedure,
            HandleLease);

        _dispatcher.RegisterJsonUnary<TableLeaseCompleteRequest, TableLeaseAcknowledgeResponse>(
            _completeProcedure,
            HandleComplete);

        _dispatcher.RegisterJsonUnary<TableLeaseHeartbeatRequest, TableLeaseAcknowledgeResponse>(
            _heartbeatProcedure,
            HandleHeartbeat);

        _dispatcher.RegisterJsonUnary<TableLeaseFailRequest, TableLeaseAcknowledgeResponse>(
            _failProcedure,
            HandleFail);

        _dispatcher.RegisterJsonUnary<TableLeaseDrainRequest, TableLeaseDrainResponse>(
            _drainProcedure,
            HandleDrain);

        _dispatcher.RegisterJsonUnary<TableLeaseRestoreRequest, TableLeaseRestoreResponse>(
            _restoreProcedure,
            HandleRestore);
    }

    private async ValueTask<TableLeaseEnqueueResponse> HandleEnqueue(JsonUnaryContext context, TableLeaseEnqueueRequest request)
    {
        await WaitForBackpressureAsync(context.CancellationToken).ConfigureAwait(false);

        if (request.Payload is null)
        {
            throw new ResultException(Error.From("table lease payload is required", "error.tablelease.payload_missing", cause: null!, metadata: null));
        }

        var workItem = TableLeaseWorkItem.FromPayload(request.Payload);
        var enqueue = await _safeQueue.EnqueueAsync(workItem, context.CancellationToken).ConfigureAwait(false);
        enqueue.ThrowIfFailure();

        await PublishReplicationAsync(
            TableLeaseReplicationEventType.Enqueue,
            ownership: null,
            ResolvePeerId(context.RequestMeta, null),
            workItem.ToPayload(),
            error: null,
            additionalMetadata: null,
            context.CancellationToken).ConfigureAwait(false);

        EvaluateBackpressure();
        return new TableLeaseEnqueueResponse(GetStats());
    }

    private async ValueTask<TableLeaseLeaseResponse> HandleLease(JsonUnaryContext context, TableLeaseLeaseRequest request)
    {
        var leaseResult = await _safeQueue.LeaseAsync(context.CancellationToken).ConfigureAwait(false);
        var lease = leaseResult.ValueOrThrow();

        if (!_leases.TryAdd(lease.OwnershipToken, lease))
        {
            // Extremely unlikely: duplicate token. Fail the lease so it is re-queued.
            await lease.FailAsync(
                Error.From("duplicate ownership token detected", "error.tablelease.duplicate_token", cause: null!, metadata: null),
                requeue: true,
                context.CancellationToken).ConfigureAwait(false);

            throw new ResultException(Error.From("duplicate ownership token detected", "error.tablelease.duplicate_token", cause: null!, metadata: null));
        }

        var ownerPeerId = ResolvePeerId(context.RequestMeta, request?.PeerId);
        var leaseHandle = TableLeaseOwnershipHandle.FromToken(lease.OwnershipToken);

        if (!string.IsNullOrWhiteSpace(ownerPeerId))
        {
            _leaseOwners[lease.OwnershipToken] = ownerPeerId!;
            var peerHandle = ToPeerHandle(leaseHandle);
            _leaseHealthTracker?.RecordLeaseAssignment(ownerPeerId!, peerHandle, lease.Value.Namespace, lease.Value.Table);
            if (lease.Value.Attributes.Count > 0)
            {
                _leaseHealthTracker?.RecordGossip(ownerPeerId!, lease.Value.Attributes);
            }
        }

        await PublishReplicationAsync(
            TableLeaseReplicationEventType.LeaseGranted,
            leaseHandle,
            ownerPeerId,
            lease.Value.ToPayload(),
            TableLeaseErrorInfo.FromError(lease.LastError),
            additionalMetadata: null,
            context.CancellationToken).ConfigureAwait(false);

        EvaluateBackpressure();
        return TableLeaseLeaseResponse.FromLease(lease, ownerPeerId);
    }

    private async ValueTask<TableLeaseAcknowledgeResponse> HandleComplete(JsonUnaryContext context, TableLeaseCompleteRequest request)
    {
        if (!TryGetLease(request.OwnershipToken, out var lease))
        {
            return TableLeaseAcknowledgeResponse.NotFound("error.tablelease.unknown_token", "Lease token was not found.");
        }

        var ownerId = TryGetLeaseOwner(request.OwnershipToken, out var resolvedOwner) ? resolvedOwner : null;

        var complete = await lease!.CompleteAsync(context.CancellationToken).ConfigureAwait(false);
        if (complete.IsFailure)
        {
            return TableLeaseAcknowledgeResponse.FromError(complete.Error!);
        }

        CleanupLease(request.OwnershipToken, ownerId, requeued: false);

        await PublishReplicationAsync(
            TableLeaseReplicationEventType.Completed,
            request.OwnershipToken,
            ownerId,
            lease.Value.ToPayload(),
            error: null,
            additionalMetadata: null,
            context.CancellationToken).ConfigureAwait(false);

        await PublishReplicationAsync(
            TableLeaseReplicationEventType.Heartbeat,
            request.OwnershipToken,
            ownerId,
            payload: null,
            error: null,
            additionalMetadata: null,
            context.CancellationToken).ConfigureAwait(false);

        EvaluateBackpressure();
        return TableLeaseAcknowledgeResponse.Ack();
    }

    private async ValueTask<TableLeaseAcknowledgeResponse> HandleHeartbeat(JsonUnaryContext context, TableLeaseHeartbeatRequest request)
    {
        if (!TryGetLease(request.OwnershipToken, out var lease))
        {
            return TableLeaseAcknowledgeResponse.NotFound("error.tablelease.unknown_token", "Lease token was not found.");
        }

        var heartbeat = await lease!.HeartbeatAsync(context.CancellationToken).ConfigureAwait(false);
        if (heartbeat.IsFailure)
        {
            return TableLeaseAcknowledgeResponse.FromError(heartbeat.Error!);
        }

        string? ownerId = null;
        if (TryGetLeaseOwner(request.OwnershipToken, out var resolvedHeartbeatOwner) && resolvedHeartbeatOwner is not null)
        {
            ownerId = resolvedHeartbeatOwner;
            _leaseHealthTracker?.RecordLeaseHeartbeat(resolvedHeartbeatOwner, ToPeerHandle(request.OwnershipToken), _queue.PendingCount);
        }

        return TableLeaseAcknowledgeResponse.Ack();
    }

    private async ValueTask<TableLeaseAcknowledgeResponse> HandleFail(JsonUnaryContext context, TableLeaseFailRequest request)
    {
        if (!TryGetLease(request.OwnershipToken, out var lease))
        {
            return TableLeaseAcknowledgeResponse.NotFound("error.tablelease.unknown_token", "Lease token was not found.");
        }

        var error = request.ToError();
        var ownerId = TryGetLeaseOwner(request.OwnershipToken, out var resolvedOwner) ? resolvedOwner : null;

        var fail = await lease!.FailAsync(error, request.Requeue, context.CancellationToken).ConfigureAwait(false);
        if (fail.IsFailure)
        {
            return TableLeaseAcknowledgeResponse.FromError(fail.Error!);
        }

        CleanupLease(request.OwnershipToken, ownerId, requeued: request.Requeue);
        if (!request.Requeue && ownerId is not null)
        {
            _leaseHealthTracker?.RecordDisconnect(ownerId, request.Reason);
        }

        await PublishReplicationAsync(
            TableLeaseReplicationEventType.Failed,
            request.OwnershipToken,
            ownerId,
            lease.Value.ToPayload(),
            TableLeaseErrorInfo.FromError(error),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "failure.requeued", request.Requeue.ToString(CultureInfo.InvariantCulture) }
            },
            context.CancellationToken).ConfigureAwait(false);

        EvaluateBackpressure();
        return TableLeaseAcknowledgeResponse.Ack();
    }

    private async ValueTask<TableLeaseDrainResponse> HandleDrain(JsonUnaryContext context, TableLeaseDrainRequest request)
    {
        var drained = await _queue.DrainPendingItemsAsync(context.CancellationToken).ConfigureAwait(false);
        var payloads = drained
            .Select(TableLeasePendingItemDto.FromPending)
            .ToImmutableArray();

        // Drain removes the work from the queue; reset stats so operators can see the impact.
        var drainMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "drain.count", payloads.Length.ToString(CultureInfo.InvariantCulture) }
        };

        await PublishReplicationAsync(
            TableLeaseReplicationEventType.DrainSnapshot,
            ownership: null,
            peerId: null,
            payload: null,
            error: null,
            drainMetadata,
            context.CancellationToken).ConfigureAwait(false);

        EvaluateBackpressure();
        return new TableLeaseDrainResponse(payloads);
    }

    private async ValueTask<TableLeaseRestoreResponse> HandleRestore(JsonUnaryContext context, TableLeaseRestoreRequest request)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            return new TableLeaseRestoreResponse(0);
        }

        var pending = request.Items
            .Select(item => item.ToPending())
            .ToList();

        await _queue.RestorePendingItemsAsync(pending, context.CancellationToken).ConfigureAwait(false);
        var restoreMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "restore.count", pending.Count.ToString(CultureInfo.InvariantCulture) }
        };

        await PublishReplicationAsync(
            TableLeaseReplicationEventType.RestoreSnapshot,
            ownership: null,
            peerId: null,
            payload: null,
            error: null,
            restoreMetadata,
            context.CancellationToken).ConfigureAwait(false);

        EvaluateBackpressure();
        return new TableLeaseRestoreResponse(pending.Count);
    }

    private TableLeaseQueueStats GetStats()
    {
        var stats = new TableLeaseQueueStats(_queue.PendingCount, _queue.ActiveLeaseCount);
        TableLeaseMetrics.RecordQueueStats(stats.PendingCount, stats.ActiveLeaseCount);
        return stats;
    }

    private bool TryGetLease(TableLeaseOwnershipHandle? handle, out SafeTaskQueueLease<TableLeaseWorkItem>? lease)
    {
        if (handle is null)
        {
            lease = null;
            return false;
        }

        var token = handle.ToToken();
        if (_leases.TryGetValue(token, out var existing))
        {
            lease = existing;
            return true;
        }

        lease = null;
        return false;
    }

    private bool TryGetLeaseOwner(TableLeaseOwnershipHandle? handle, out string? ownerId)
    {
        ownerId = null;
        if (handle is null)
        {
            return false;
        }

        return _leaseOwners.TryGetValue(handle.ToToken(), out ownerId);
    }

    private void CleanupLease(TableLeaseOwnershipHandle? handle, string? explicitOwner = null, bool requeued = false)
    {
        if (handle is null)
        {
            return;
        }

        var token = handle.ToToken();
        _leases.TryRemove(token, out _);

        var owner = explicitOwner;
        if (owner is null && _leaseOwners.TryGetValue(token, out var trackedOwner))
        {
            owner = trackedOwner;
        }

        _leaseOwners.TryRemove(token, out _);

        if (owner is not null)
        {
            _leaseHealthTracker?.RecordLeaseReleased(owner, ToPeerHandle(handle), requeued);
        }
    }

    private void EvaluateBackpressure()
    {
        if (!_backpressureHighWatermark.HasValue)
        {
            return;
        }

        var pending = _queue.PendingCount;
        if (pending >= _backpressureHighWatermark.Value)
        {
            TryActivateBackpressure(pending);
            return;
        }

        var lowWatermark = _backpressureLowWatermark ?? Math.Max(0, _backpressureHighWatermark.Value / 2);
        if (pending <= lowWatermark)
        {
            TryDeactivateBackpressure(pending);
        }
    }

    private void TryActivateBackpressure(long pending)
    {
        if (_isBackpressureActive == 1)
        {
            return;
        }

        if (!CanTransitionBackpressure())
        {
            return;
        }

        if (Interlocked.Exchange(ref _isBackpressureActive, 1) == 1)
        {
            return;
        }

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _backpressureTcs, gate);
        PublishBackpressureSignal(true, pending);
    }

    private void TryDeactivateBackpressure(long pending)
    {
        if (_isBackpressureActive == 0)
        {
            return;
        }

        if (!CanTransitionBackpressure())
        {
            return;
        }

        if (Interlocked.Exchange(ref _isBackpressureActive, 0) == 0)
        {
            return;
        }

        var gate = Interlocked.Exchange(ref _backpressureTcs, null);
        gate?.TrySetResult(true);
        PublishBackpressureSignal(false, pending);
    }

    private bool CanTransitionBackpressure()
    {
        if (!_backpressureCooldown.HasValue)
        {
            return true;
        }

        if (_lastBackpressureTransition == default)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        return now - _lastBackpressureTransition >= _backpressureCooldown.Value;
    }

    private void PublishBackpressureSignal(bool isActive, long pending)
    {
        _lastBackpressureTransition = DateTimeOffset.UtcNow;
        TableLeaseMetrics.RecordBackpressureState(isActive);

        if (_backpressureListener is not null)
        {
            var signal = new TableLeaseBackpressureSignal(
                isActive,
                pending,
                _lastBackpressureTransition,
                _backpressureHighWatermark,
                _backpressureLowWatermark);

            var _ = _backpressureListener.OnBackpressureChanged(signal, CancellationToken.None).AsTask();
        }
    }

    private async ValueTask WaitForBackpressureAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _isBackpressureActive) == 1)
        {
            var gate = Volatile.Read(ref _backpressureTcs);
            if (gate is null)
            {
                break;
            }

            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask PublishReplicationAsync(
        TableLeaseReplicationEventType eventType,
        TableLeaseOwnershipHandle? ownership,
        string? peerId,
        TableLeaseItemPayload? payload,
        TableLeaseErrorInfo? error,
        IReadOnlyDictionary<string, string>? additionalMetadata,
        CancellationToken cancellationToken)
    {
        var metadata = MergeMetadata(additionalMetadata);
        var replicationEvent = TableLeaseReplicationEvent.Create(
            eventType,
            ownership,
            string.IsNullOrWhiteSpace(peerId) ? null : peerId,
            payload,
            error,
            metadata);

        if (_replicator is not null)
        {
            await _replicator.PublishAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
        }

        if (_deterministicCoordinator is not null)
        {
            await _deterministicCoordinator.RecordAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyDictionary<string, string> MergeMetadata(IReadOnlyDictionary<string, string>? additional)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["queue.pending"] = _queue.PendingCount.ToString(CultureInfo.InvariantCulture),
            ["queue.active"] = _queue.ActiveLeaseCount.ToString(CultureInfo.InvariantCulture)
        };

        if (additional is not null)
        {
            foreach (var kvp in additional)
            {
                snapshot[kvp.Key] = kvp.Value;
            }
        }

        return snapshot;
    }

    private static PeerLeaseHandle ToPeerHandle(TableLeaseOwnershipHandle handle) =>
        PeerLeaseHandle.FromToken(handle.ToToken());

    private static PeerLeaseHandle ToPeerHandle(TaskQueueOwnershipToken token) =>
        PeerLeaseHandle.FromToken(token);

    private static string ResolvePeerId(RequestMeta meta, string? explicitPeer)
    {
        if (!string.IsNullOrWhiteSpace(explicitPeer))
        {
            return explicitPeer!;
        }

        if (!string.IsNullOrWhiteSpace(meta.Caller))
        {
            return meta.Caller!;
        }

        if (meta.Headers.TryGetValue(PrincipalBindingOptions.DefaultPrincipalMetadataKey, out var principal) &&
            !string.IsNullOrWhiteSpace(principal))
        {
            return principal!;
        }

        if (meta.Headers.TryGetValue("x-peer-id", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            return header!;
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var lease in _leases.Values)
        {
            try
            {
                await lease.FailAsync(
                    Error.Canceled("lease disposed", token: null),
                    requeue: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignored: we are disposing and best-effort requeueing outstanding work.
            }
        }

        _leases.Clear();
        await _safeQueue.DisposeAsync().ConfigureAwait(false);
        await _queue.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Options used by <see cref="TableLeaseDispatcherComponent"/>.
/// </summary>
public sealed class TableLeaseDispatcherOptions
{
    /// <summary>The namespace prefix applied to the registered procedures. Defaults to 'tablelease'.</summary>
    public string Namespace { get; init; } = "tablelease";

    /// <summary>Task queue options that control capacity, lease duration, and heartbeat cadence.</summary>
    public TaskQueueOptions? QueueOptions { get; init; } = new();

    /// <summary>Optional tracker used for peer membership gossip and health propagation.</summary>
    public PeerLeaseHealthTracker? LeaseHealthTracker { get; init; }

    /// <summary>Optional replication hub used to broadcast ordered lease events.</summary>
    public ITableLeaseReplicator? Replicator { get; init; }

    /// <summary>Optional deterministic coordinator used to persist replication effects.</summary>
    public ITableLeaseDeterministicCoordinator? DeterministicCoordinator { get; init; }

    /// <summary>Convenience options to spawn a <see cref="DeterministicTableLeaseCoordinator"/>.</summary>
    public TableLeaseDeterministicOptions? DeterministicOptions { get; init; }

    /// <summary>Optional listener invoked whenever SafeTaskQueue backpressure toggles.</summary>
    public ITableLeaseBackpressureListener? BackpressureListener { get; init; }
}

/// <summary>Represents the serialized form of a work item stored in the queue.</summary>
public sealed record TableLeaseItemPayload(
    string Namespace,
    string Table,
    string PartitionKey,
    string PayloadEncoding,
    byte[] Body,
    IReadOnlyDictionary<string, string>? Attributes = null,
    string? RequestId = null);

public sealed record TableLeaseEnqueueRequest(TableLeaseItemPayload? Payload);

public sealed record TableLeaseEnqueueResponse(TableLeaseQueueStats Stats);

public sealed record TableLeaseLeaseRequest(string? PeerId = null);

public sealed record TableLeaseLeaseResponse(
    TableLeaseItemPayload Payload,
    long SequenceId,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    TableLeaseErrorInfo? LastError,
    TableLeaseOwnershipHandle OwnershipToken,
    string? OwnerPeerId)
{
    internal static TableLeaseLeaseResponse FromLease(
        SafeTaskQueueLease<TableLeaseWorkItem> lease,
        string? peerId)
    {
        var payload = lease.Value.ToPayload();
        return new TableLeaseLeaseResponse(
            payload,
            lease.SequenceId,
            lease.Attempt,
            lease.EnqueuedAt,
            TableLeaseErrorInfo.FromError(lease.LastError),
            TableLeaseOwnershipHandle.FromToken(lease.OwnershipToken),
            peerId);
    }
}

public sealed record TableLeaseCompleteRequest(TableLeaseOwnershipHandle OwnershipToken);

public sealed record TableLeaseHeartbeatRequest(TableLeaseOwnershipHandle OwnershipToken);

public sealed record TableLeaseFailRequest(
    TableLeaseOwnershipHandle OwnershipToken,
    string? Reason,
    string? ErrorCode,
    bool Requeue = true,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public Error ToError()
    {
        var message = string.IsNullOrWhiteSpace(Reason) ? "lease failed" : Reason!;
        var code = string.IsNullOrWhiteSpace(ErrorCode) ? "error.tablelease.failed" : ErrorCode!;

        IReadOnlyDictionary<string, object?>? metadata = null;
        if (Metadata is { Count: > 0 })
        {
            metadata = Metadata.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        return Error.From(message, code, cause: null!, metadata);
    }
}

public sealed record TableLeaseDrainRequest;

public sealed record TableLeaseDrainResponse(IReadOnlyList<TableLeasePendingItemDto> Items);

public sealed record TableLeaseRestoreRequest(IReadOnlyList<TableLeasePendingItemDto> Items);

public sealed record TableLeaseRestoreResponse(int RestoredCount);

public sealed record TableLeaseQueueStats(long PendingCount, long ActiveLeaseCount);

public sealed record TableLeaseAcknowledgeResponse(bool Success, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static TableLeaseAcknowledgeResponse Ack() => new(true);

    public static TableLeaseAcknowledgeResponse NotFound(string code, string message) =>
        new(false, code, message);

    public static TableLeaseAcknowledgeResponse FromError(Error error) =>
        new(false, error.Code, error.Message);
}

public sealed record TableLeasePendingItemDto(
    TableLeaseItemPayload Payload,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    TableLeaseErrorInfo? LastError,
    long SequenceId,
    TableLeaseOwnershipHandle? LastOwnershipToken)
{
    public static TableLeasePendingItemDto FromPending(TaskQueuePendingItem<TableLeaseWorkItem> pending)
    {
        var payload = pending.Value.ToPayload();
        var handle = pending.LastOwnershipToken.HasValue
            ? TableLeaseOwnershipHandle.FromToken(pending.LastOwnershipToken.Value)
            : null;

        return new TableLeasePendingItemDto(
            payload,
            pending.Attempt,
            pending.EnqueuedAt,
            TableLeaseErrorInfo.FromError(pending.LastError),
            pending.SequenceId,
            handle);
    }

    public TaskQueuePendingItem<TableLeaseWorkItem> ToPending()
    {
        var workItem = TableLeaseWorkItem.FromPayload(Payload);
        var lastToken = LastOwnershipToken?.ToToken();
        var error = LastError?.ToError() ?? Error.Unspecified("restored pending item");
        return new TaskQueuePendingItem<TableLeaseWorkItem>(
            workItem,
            Attempt,
            EnqueuedAt,
            error,
            SequenceId,
            lastToken);
    }
}

public sealed record TableLeaseOwnershipHandle(long SequenceId, int Attempt, Guid LeaseId)
{
    public TaskQueueOwnershipToken ToToken() => new(SequenceId, Attempt, LeaseId);

    public static TableLeaseOwnershipHandle FromToken(TaskQueueOwnershipToken token) =>
        new(token.SequenceId, token.Attempt, token.LeaseId);
}

public sealed record TableLeaseErrorInfo(string Message, string? Code)
{
    public static TableLeaseErrorInfo? FromError(Error? error)
    {
        if (error is null)
        {
            return null;
        }

        return new TableLeaseErrorInfo(error.Message, error.Code);
    }

    public Error ToError()
    {
        var code = string.IsNullOrWhiteSpace(Code) ? "error.tablelease.pending" : Code!;
        return Error.From(Message, code, cause: null!, metadata: null);
    }
}

public sealed record TableLeaseWorkItem(
    string Namespace,
    string Table,
    string PartitionKey,
    string PayloadEncoding,
    byte[] Body,
    ImmutableDictionary<string, string> Attributes,
    string? RequestId)
{
    public static TableLeaseWorkItem FromPayload(TableLeaseItemPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(payload.Namespace))
        {
            throw new ArgumentException("Namespace is required.", nameof(payload));
        }

        if (string.IsNullOrWhiteSpace(payload.Table))
        {
            throw new ArgumentException("Table is required.", nameof(payload));
        }

        if (string.IsNullOrWhiteSpace(payload.PartitionKey))
        {
            throw new ArgumentException("PartitionKey is required.", nameof(payload));
        }

        if (string.IsNullOrWhiteSpace(payload.PayloadEncoding))
        {
            throw new ArgumentException("Payload encoding is required.", nameof(payload));
        }

        var attributes = payload.Attributes is null
            ? ImmutableDictionary<string, string>.Empty
            : payload.Attributes.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var body = payload.Body ?? Array.Empty<byte>();

        return new TableLeaseWorkItem(
            payload.Namespace,
            payload.Table,
            payload.PartitionKey,
            payload.PayloadEncoding,
            body,
            attributes,
            payload.RequestId);
    }

    public TableLeaseItemPayload ToPayload()
    {
        var attributes = Attributes.Count == 0
            ? ImmutableDictionary<string, string>.Empty
            : Attributes;

        return new TableLeaseItemPayload(
            Namespace,
            Table,
            PartitionKey,
            PayloadEncoding,
            Body,
            attributes,
            RequestId);
    }
}
