using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Hugo;
using OmniRelay.ControlPlane.Throttling;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Diagnostics;
using static Hugo.Go;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Hosts a SafeTaskQueue-backed resource lease queue and exposes canonical procedures for enqueue, lease, ack, and drain flows.
/// </summary>
public sealed class ResourceLeaseDispatcherComponent : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ResourceLeaseDispatcherOptions _options;
    private readonly TaskQueue<ResourceLeaseWorkItem> _queue;
    private readonly SafeTaskQueueWrapper<ResourceLeaseWorkItem> _safeQueue;
    private readonly ConcurrentDictionary<TaskQueueOwnershipToken, SafeTaskQueueLease<ResourceLeaseWorkItem>> _leases = new();
    private readonly ConcurrentDictionary<TaskQueueOwnershipToken, string> _leaseOwners = new();
    private readonly PeerLeaseHealthTracker? _leaseHealthTracker;
    private readonly IResourceLeaseReplicator? _replicator;
    private readonly IResourceLeaseBackpressureListener? _backpressureListener;
    private readonly IResourceLeaseDeterministicCoordinator? _deterministicCoordinator;
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
    /// Creates a resource lease component and immediately registers the standard procedures on the supplied dispatcher.
    /// </summary>
    public ResourceLeaseDispatcherComponent(Dispatcher dispatcher, ResourceLeaseDispatcherOptions options)
        : this(dispatcher, options, ResolveDeterministicCoordinatorOrThrow(options))
    {
    }

    private ResourceLeaseDispatcherComponent(
        Dispatcher dispatcher,
        ResourceLeaseDispatcherOptions options,
        IResourceLeaseDeterministicCoordinator? deterministicCoordinator)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var queueOptions = _options.QueueOptions ?? new TaskQueueOptions();
        _backpressureHighWatermark = queueOptions.Backpressure?.HighWatermark;
        _backpressureLowWatermark = queueOptions.Backpressure?.LowWatermark;
        _backpressureCooldown = queueOptions.Backpressure?.Cooldown;

        var prefix = string.IsNullOrWhiteSpace(_options.Namespace)
            ? "resourcelease"
            : _options.Namespace.Trim();

        _enqueueProcedure = $"{prefix}::enqueue";
        _leaseProcedure = $"{prefix}::lease";
        _completeProcedure = $"{prefix}::complete";
        _heartbeatProcedure = $"{prefix}::heartbeat";
        _failProcedure = $"{prefix}::fail";
        _drainProcedure = $"{prefix}::drain";
        _restoreProcedure = $"{prefix}::restore";

        _queue = new TaskQueue<ResourceLeaseWorkItem>(queueOptions);
        _safeQueue = new SafeTaskQueueWrapper<ResourceLeaseWorkItem>(_queue, ownsQueue: false);
        _leaseHealthTracker = _options.LeaseHealthTracker;
        _replicator = _options.Replicator;
        _backpressureListener = _options.BackpressureListener;
        _deterministicCoordinator = deterministicCoordinator;

        RegisterProcedures();
    }

    public static Result<ResourceLeaseDispatcherComponent> Create(Dispatcher dispatcher, ResourceLeaseDispatcherOptions options)
    {
        if (dispatcher is null)
        {
            return Err<ResourceLeaseDispatcherComponent>(ResourceLeaseDeterministicErrors.DispatcherRequired());
        }

        if (options is null)
        {
            return Err<ResourceLeaseDispatcherComponent>(ResourceLeaseDeterministicErrors.DispatcherOptionsRequired());
        }

        var deterministicCoordinator = ResolveDeterministicCoordinator(options);
        if (deterministicCoordinator.IsFailure)
        {
            return Err<ResourceLeaseDispatcherComponent>(deterministicCoordinator.Error!);
        }

        return Ok(new ResourceLeaseDispatcherComponent(dispatcher, options, deterministicCoordinator.Value));
    }

    private void RegisterProcedures()
    {
        _dispatcher.RegisterJsonUnary<ResourceLeaseEnqueueRequest, ResourceLeaseEnqueueResponse>(
            _enqueueProcedure,
            HandleEnqueue,
            ConfigureResourceLeaseCodec);

        _dispatcher.RegisterJsonUnary<ResourceLeaseLeaseRequest, ResourceLeaseLeaseResponse>(
            _leaseProcedure,
            HandleLease,
            ConfigureResourceLeaseCodec);

        _dispatcher.RegisterJsonUnary<ResourceLeaseCompleteRequest, ResourceLeaseAcknowledgeResponse>(
            _completeProcedure,
            HandleComplete,
            ConfigureResourceLeaseCodec);

        _dispatcher.RegisterJsonUnary<ResourceLeaseHeartbeatRequest, ResourceLeaseAcknowledgeResponse>(
            _heartbeatProcedure,
            HandleHeartbeat,
            ConfigureResourceLeaseCodec);

        _dispatcher.RegisterJsonUnary<ResourceLeaseFailRequest, ResourceLeaseAcknowledgeResponse>(
            _failProcedure,
            HandleFail,
            ConfigureResourceLeaseCodec);

        _dispatcher.RegisterJsonUnary<ResourceLeaseDrainRequest, ResourceLeaseDrainResponse>(
            _drainProcedure,
            HandleDrain,
            ConfigureResourceLeaseCodec);

        _dispatcher.RegisterJsonUnary<ResourceLeaseRestoreRequest, ResourceLeaseRestoreResponse>(
            _restoreProcedure,
            HandleRestore,
            ConfigureResourceLeaseCodec);
    }

    private static void ConfigureResourceLeaseCodec<TRequest, TResponse>(JsonCodecBuilder<TRequest, TResponse> builder) =>
        builder.SerializerContext ??= ResourceLeaseJson.Context;

    private async ValueTask<Result<ResourceLeaseEnqueueResponse>> HandleEnqueue(JsonUnaryContext context, ResourceLeaseEnqueueRequest request)
    {
        await WaitForBackpressureAsync(context.CancellationToken).ConfigureAwait(false);

        if (request.Payload is null)
        {
            return Err<ResourceLeaseEnqueueResponse>(ResourceLeaseErrors.PayloadRequired());
        }

        var workItemResult = ResourceLeaseWorkItem.FromPayload(request.Payload);
        if (workItemResult.IsFailure)
        {
            return Err<ResourceLeaseEnqueueResponse>(workItemResult.Error);
        }

        var workItem = workItemResult.Value;

        var enqueue = await _safeQueue.EnqueueAsync(workItem, context.CancellationToken).ConfigureAwait(false);
        if (enqueue.IsFailure)
        {
            return Err<ResourceLeaseEnqueueResponse>(enqueue.Error!);
        }

        var replicationResult = await PublishReplicationAsync(
            ResourceLeaseReplicationEventType.Enqueue,
            ownership: null,
            ResolvePeerId(context.RequestMeta, null),
            workItem.ToPayload(),
            error: null,
            additionalMetadata: null,
            context.CancellationToken).ConfigureAwait(false);
        if (replicationResult.IsFailure)
        {
            return replicationResult.CastFailure<ResourceLeaseEnqueueResponse>();
        }

        EvaluateBackpressure();
        return Ok(new ResourceLeaseEnqueueResponse(GetStats()));
    }

    private async ValueTask<Result<ResourceLeaseLeaseResponse>> HandleLease(JsonUnaryContext context, ResourceLeaseLeaseRequest request)
    {
        var leaseResult = await _safeQueue.LeaseAsync(context.CancellationToken).ConfigureAwait(false);
        if (leaseResult.IsFailure)
        {
            return Err<ResourceLeaseLeaseResponse>(leaseResult.Error!);
        }

        var lease = leaseResult.Value;

        if (!_leases.TryAdd(lease.OwnershipToken, lease))
        {
            // Extremely unlikely: duplicate token. Fail the lease so it is re-queued.
            await lease.FailAsync(
                Error.From("duplicate ownership token detected", "error.resourcelease.duplicate_token", cause: null!, metadata: (IReadOnlyDictionary<string, object?>?)null),
                requeue: true,
                context.CancellationToken).ConfigureAwait(false);

            return Err<ResourceLeaseLeaseResponse>(Error.From(
                "duplicate ownership token detected",
                "error.resourcelease.duplicate_token",
                cause: null!,
                metadata: (IReadOnlyDictionary<string, object?>?)null));
        }

        var ownerPeerId = ResolvePeerId(context.RequestMeta, request?.PeerId);
        var leaseHandle = ResourceLeaseOwnershipHandle.FromToken(lease.OwnershipToken);

        if (!string.IsNullOrWhiteSpace(ownerPeerId))
        {
            _leaseOwners[lease.OwnershipToken] = ownerPeerId!;
            var peerHandle = ToPeerHandle(leaseHandle);
            _leaseHealthTracker?.RecordLeaseAssignment(ownerPeerId!, peerHandle, lease.Value.ResourceType, lease.Value.ResourceId);
            if (lease.Value.Attributes.Count > 0)
            {
                _leaseHealthTracker?.RecordGossip(ownerPeerId!, lease.Value.Attributes);
            }
        }

        var replicationResult = await PublishReplicationAsync(
            ResourceLeaseReplicationEventType.LeaseGranted,
            leaseHandle,
            ownerPeerId,
            lease.Value.ToPayload(),
            ResourceLeaseErrorInfo.FromError(lease.LastError),
            additionalMetadata: null,
            context.CancellationToken).ConfigureAwait(false);
        if (replicationResult.IsFailure)
        {
            return replicationResult.CastFailure<ResourceLeaseLeaseResponse>();
        }

        EvaluateBackpressure();
        return Ok(ResourceLeaseLeaseResponse.FromLease(lease, ownerPeerId));
    }

    private async ValueTask<Result<ResourceLeaseAcknowledgeResponse>> HandleComplete(JsonUnaryContext context, ResourceLeaseCompleteRequest request)
    {
        if (!TryGetLease(request.OwnershipToken, out var lease))
        {
            return Err<ResourceLeaseAcknowledgeResponse>(Error.From(
                "Lease token was not found.",
                "error.resourcelease.unknown_token",
                cause: null!,
                metadata: (IReadOnlyDictionary<string, object?>?)null));
        }

        var ownerId = TryGetLeaseOwner(request.OwnershipToken, out var resolvedOwner) ? resolvedOwner : null;

        var complete = await lease!.CompleteAsync(context.CancellationToken).ConfigureAwait(false);
        if (complete.IsFailure)
        {
            return Err<ResourceLeaseAcknowledgeResponse>(complete.Error!);
        }

        CleanupLease(request.OwnershipToken, ownerId, requeued: false);

        var completionReplication = await PublishReplicationAsync(
            ResourceLeaseReplicationEventType.Completed,
            request.OwnershipToken,
            ownerId,
            lease.Value.ToPayload(),
            error: null,
            additionalMetadata: null,
            context.CancellationToken).ConfigureAwait(false);
        if (completionReplication.IsFailure)
        {
            return completionReplication.CastFailure<ResourceLeaseAcknowledgeResponse>();
        }

        var heartbeatReplication = await PublishReplicationAsync(
            ResourceLeaseReplicationEventType.Heartbeat,
            request.OwnershipToken,
            ownerId,
            payload: null,
            error: null,
            additionalMetadata: null,
            context.CancellationToken).ConfigureAwait(false);
        if (heartbeatReplication.IsFailure)
        {
            return heartbeatReplication.CastFailure<ResourceLeaseAcknowledgeResponse>();
        }

        EvaluateBackpressure();
        return Ok(ResourceLeaseAcknowledgeResponse.Ack());
    }

    private async ValueTask<Result<ResourceLeaseAcknowledgeResponse>> HandleHeartbeat(JsonUnaryContext context, ResourceLeaseHeartbeatRequest request)
    {
        if (!TryGetLease(request.OwnershipToken, out var lease))
        {
            return Err<ResourceLeaseAcknowledgeResponse>(Error.From(
                "Lease token was not found.",
                "error.resourcelease.unknown_token",
                cause: null!,
                metadata: (IReadOnlyDictionary<string, object?>?)null));
        }

        var heartbeat = await lease!.HeartbeatAsync(context.CancellationToken).ConfigureAwait(false);
        if (heartbeat.IsFailure)
        {
            return Err<ResourceLeaseAcknowledgeResponse>(heartbeat.Error!);
        }

        if (TryGetLeaseOwner(request.OwnershipToken, out var resolvedHeartbeatOwner) && resolvedHeartbeatOwner is not null)
        {
            _leaseHealthTracker?.RecordLeaseHeartbeat(resolvedHeartbeatOwner, ToPeerHandle(request.OwnershipToken), _queue.PendingCount);
        }

        return Ok(ResourceLeaseAcknowledgeResponse.Ack());
    }

    private async ValueTask<Result<ResourceLeaseAcknowledgeResponse>> HandleFail(JsonUnaryContext context, ResourceLeaseFailRequest request)
    {
        if (!TryGetLease(request.OwnershipToken, out var lease))
        {
            return Err<ResourceLeaseAcknowledgeResponse>(Error.From(
                "Lease token was not found.",
                "error.resourcelease.unknown_token",
                cause: null!,
                metadata: (IReadOnlyDictionary<string, object?>?)null));
        }

        var error = request.ToError();
        var ownerId = TryGetLeaseOwner(request.OwnershipToken, out var resolvedOwner) ? resolvedOwner : null;

        var fail = await lease!.FailAsync(error, request.Requeue, context.CancellationToken).ConfigureAwait(false);
        if (fail.IsFailure)
        {
            return Err<ResourceLeaseAcknowledgeResponse>(fail.Error!);
        }

        CleanupLease(request.OwnershipToken, ownerId, requeued: request.Requeue);
        if (!request.Requeue && ownerId is not null)
        {
            _leaseHealthTracker?.RecordDisconnect(ownerId, request.Reason);
        }

        var replicationResult = await PublishReplicationAsync(
            ResourceLeaseReplicationEventType.Failed,
            request.OwnershipToken,
            ownerId,
            lease.Value.ToPayload(),
            ResourceLeaseErrorInfo.FromError(error),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "failure.requeued", request.Requeue.ToString(CultureInfo.InvariantCulture) }
            },
            context.CancellationToken).ConfigureAwait(false);
        if (replicationResult.IsFailure)
        {
            return replicationResult.CastFailure<ResourceLeaseAcknowledgeResponse>();
        }

        EvaluateBackpressure();
        return Ok(ResourceLeaseAcknowledgeResponse.Ack());
    }

    private async ValueTask<Result<ResourceLeaseDrainResponse>> HandleDrain(JsonUnaryContext context, ResourceLeaseDrainRequest request)
    {
        var drained = await _queue.DrainPendingItemsAsync(context.CancellationToken).ConfigureAwait(false);
        var payloads = drained
            .Select(ResourceLeasePendingItemDto.FromPending)
            .ToImmutableArray();

        // Drain removes the work from the queue; reset stats so operators can see the impact.
        var drainMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "drain.count", payloads.Length.ToString(CultureInfo.InvariantCulture) }
        };

        var replicationResult = await PublishReplicationAsync(
            ResourceLeaseReplicationEventType.DrainSnapshot,
            ownership: null,
            peerId: null,
            payload: null,
            error: null,
            drainMetadata,
            context.CancellationToken).ConfigureAwait(false);
        if (replicationResult.IsFailure)
        {
            return replicationResult.CastFailure<ResourceLeaseDrainResponse>();
        }

        EvaluateBackpressure();
        return Ok(new ResourceLeaseDrainResponse(payloads));
    }

    private async ValueTask<Result<ResourceLeaseRestoreResponse>> HandleRestore(JsonUnaryContext context, ResourceLeaseRestoreRequest request)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            return Ok(new ResourceLeaseRestoreResponse(0));
        }

        var pending = new List<TaskQueuePendingItem<ResourceLeaseWorkItem>>(request.Items.Count);
        foreach (var item in request.Items)
        {
            if (item is null)
            {
                return Err<ResourceLeaseRestoreResponse>(ResourceLeaseErrors.PendingItemRequired());
            }

            var pendingResult = item.ToPending();
            if (pendingResult.IsFailure)
            {
                return Err<ResourceLeaseRestoreResponse>(pendingResult.Error!);
            }

            pending.Add(pendingResult.Value);
        }

        await _queue.RestorePendingItemsAsync(pending, context.CancellationToken).ConfigureAwait(false);
        var restoreMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "restore.count", pending.Count.ToString(CultureInfo.InvariantCulture) }
        };

        var replicationResult = await PublishReplicationAsync(
            ResourceLeaseReplicationEventType.RestoreSnapshot,
            ownership: null,
            peerId: null,
            payload: null,
            error: null,
            restoreMetadata,
            context.CancellationToken).ConfigureAwait(false);
        if (replicationResult.IsFailure)
        {
            return replicationResult.CastFailure<ResourceLeaseRestoreResponse>();
        }

        EvaluateBackpressure();
        return Ok(new ResourceLeaseRestoreResponse(pending.Count));
    }

    private ResourceLeaseQueueStats GetStats()
    {
        var stats = new ResourceLeaseQueueStats(_queue.PendingCount, _queue.ActiveLeaseCount);
        ResourceLeaseMetrics.RecordQueueStats(stats.PendingCount, stats.ActiveLeaseCount);
        return stats;
    }

    private bool TryGetLease(ResourceLeaseOwnershipHandle? handle, out SafeTaskQueueLease<ResourceLeaseWorkItem>? lease)
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

    private bool TryGetLeaseOwner(ResourceLeaseOwnershipHandle? handle, out string? ownerId)
    {
        ownerId = null;
        if (handle is null)
        {
            return false;
        }

        return _leaseOwners.TryGetValue(handle.ToToken(), out ownerId);
    }

    private void CleanupLease(ResourceLeaseOwnershipHandle? handle, string? explicitOwner = null, bool requeued = false)
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
        ResourceLeaseMetrics.RecordBackpressureState(isActive);

        if (_backpressureListener is not null)
        {
            var signal = new ResourceLeaseBackpressureSignal(
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

    private async ValueTask<Result<Unit>> PublishReplicationAsync(
        ResourceLeaseReplicationEventType eventType,
        ResourceLeaseOwnershipHandle? ownership,
        string? peerId,
        ResourceLeaseItemPayload? payload,
        ResourceLeaseErrorInfo? error,
        IReadOnlyDictionary<string, string>? additionalMetadata,
        CancellationToken cancellationToken)
    {
        var metadata = MergeMetadata(additionalMetadata);
        var replicationEvent = ResourceLeaseReplicationEvent.Create(
            eventType,
            ownership,
            string.IsNullOrWhiteSpace(peerId) ? null : peerId,
            payload,
            error,
            metadata);
        ResourceLeaseReplicationMetrics.RecordReplicationEvent(replicationEvent);

        if (_replicator is not null)
        {
            var published = await PublishSafelyAsync(
                () => _replicator.PublishAsync(replicationEvent, cancellationToken),
                replicationEvent,
                "replicator.publish",
                cancellationToken).ConfigureAwait(false);

            if (published.IsFailure)
            {
                return published;
            }
        }

        if (_deterministicCoordinator is not null)
        {
            var deterministic = await PublishSafelyAsync(
                () => _deterministicCoordinator.RecordAsync(replicationEvent, cancellationToken),
                replicationEvent,
                "replicator.deterministic",
                cancellationToken).ConfigureAwait(false);

            if (deterministic.IsFailure)
            {
                return deterministic;
            }
        }

        return Ok(Unit.Value);
    }

    private Dictionary<string, string> MergeMetadata(IReadOnlyDictionary<string, string>? additional)
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

    private static async ValueTask<Result<Unit>> PublishSafelyAsync(
        Func<ValueTask<Result<Unit>>> operation,
        ResourceLeaseReplicationEvent replicationEvent,
        string stage,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await operation().ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Err<Unit>(result.Error!
                    .WithMetadata("replication.stage", stage)
                    .WithMetadata("replication.eventType", replicationEvent.EventType.ToString())
                    .WithMetadata("replication.peerId", replicationEvent.PeerId ?? string.Empty)
                    .WithMetadata("replication.sequenceHint", replicationEvent.SequenceNumber));
            }

            return result;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException oce && oce.CancellationToken == cancellationToken)
            {
                return Err<Unit>(Error.Canceled("replication canceled", cancellationToken)
                    .WithMetadata("replication.stage", stage)
                    .WithMetadata("replication.eventType", replicationEvent.EventType.ToString())
                    .WithMetadata("replication.peerId", replicationEvent.PeerId ?? string.Empty)
                    .WithMetadata("replication.sequenceHint", replicationEvent.SequenceNumber));
            }

            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", stage)
                .WithMetadata("replication.eventType", replicationEvent.EventType.ToString())
                .WithMetadata("replication.peerId", replicationEvent.PeerId ?? string.Empty)
                .WithMetadata("replication.sequenceHint", replicationEvent.SequenceNumber));
        }
    }

    private static PeerLeaseHandle ToPeerHandle(ResourceLeaseOwnershipHandle handle) =>
        PeerLeaseHandle.FromToken(handle.ToToken());

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

    private static Result<IResourceLeaseDeterministicCoordinator?> ResolveDeterministicCoordinator(ResourceLeaseDispatcherOptions options)
    {
        if (options.DeterministicCoordinator is not null)
        {
            return Ok<IResourceLeaseDeterministicCoordinator?>(options.DeterministicCoordinator);
        }

        if (options.DeterministicOptions is null)
        {
            return Ok<IResourceLeaseDeterministicCoordinator?>(null);
        }

        return DeterministicResourceLeaseCoordinator.Create(options.DeterministicOptions)
            .Map(coordinator => (IResourceLeaseDeterministicCoordinator?)coordinator);
    }

    private static IResourceLeaseDeterministicCoordinator? ResolveDeterministicCoordinatorOrThrow(ResourceLeaseDispatcherOptions options)
    {
        var result = ResolveDeterministicCoordinator(options);
        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }

        return result.Value;
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
/// Options used by <see cref="ResourceLeaseDispatcherComponent"/>.
/// </summary>
public sealed class ResourceLeaseDispatcherOptions
{
    /// <summary>The namespace prefix applied to the registered procedures. Defaults to 'resourcelease'.</summary>
    public string Namespace { get; init; } = "resourcelease";

    /// <summary>Task queue options that control capacity, lease duration, and heartbeat cadence.</summary>
    public TaskQueueOptions? QueueOptions { get; init; } = new();

    /// <summary>Optional tracker used for peer membership gossip and health propagation.</summary>
    public PeerLeaseHealthTracker? LeaseHealthTracker { get; init; }

    /// <summary>Optional replication hub used to broadcast ordered lease events.</summary>
    public IResourceLeaseReplicator? Replicator { get; init; }

    /// <summary>Optional deterministic coordinator used to persist replication effects.</summary>
    public IResourceLeaseDeterministicCoordinator? DeterministicCoordinator { get; init; }

    /// <summary>Convenience options to spawn a <see cref="DeterministicResourceLeaseCoordinator"/>.</summary>
    public ResourceLeaseDeterministicOptions? DeterministicOptions { get; init; }

    /// <summary>Optional listener invoked whenever SafeTaskQueue backpressure toggles.</summary>
    public IResourceLeaseBackpressureListener? BackpressureListener { get; init; }
}

/// <summary>Represents the serialized form of a work item stored in the queue.</summary>
public sealed record ResourceLeaseItemPayload(
    string ResourceType,
    string ResourceId,
    string PartitionKey,
    string PayloadEncoding,
    byte[] Body,
    IReadOnlyDictionary<string, string>? Attributes = null,
    string? RequestId = null);

public sealed record ResourceLeaseEnqueueRequest(ResourceLeaseItemPayload? Payload);

public sealed record ResourceLeaseEnqueueResponse(ResourceLeaseQueueStats Stats);

public sealed record ResourceLeaseLeaseRequest(string? PeerId = null);

public sealed record ResourceLeaseLeaseResponse(
    ResourceLeaseItemPayload Payload,
    long SequenceId,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    ResourceLeaseErrorInfo? LastError,
    ResourceLeaseOwnershipHandle OwnershipToken,
    string? OwnerPeerId)
{
    internal static ResourceLeaseLeaseResponse FromLease(
        SafeTaskQueueLease<ResourceLeaseWorkItem> lease,
        string? peerId)
    {
        var payload = lease.Value.ToPayload();
        return new ResourceLeaseLeaseResponse(
            payload,
            lease.SequenceId,
            lease.Attempt,
            lease.EnqueuedAt,
            ResourceLeaseErrorInfo.FromError(lease.LastError),
            ResourceLeaseOwnershipHandle.FromToken(lease.OwnershipToken),
            peerId);
    }
}

public sealed record ResourceLeaseCompleteRequest(ResourceLeaseOwnershipHandle OwnershipToken);

public sealed record ResourceLeaseHeartbeatRequest(ResourceLeaseOwnershipHandle OwnershipToken);

public sealed record ResourceLeaseFailRequest(
    ResourceLeaseOwnershipHandle OwnershipToken,
    string? Reason,
    string? ErrorCode,
    bool Requeue = true,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public Error ToError()
    {
        var message = string.IsNullOrWhiteSpace(Reason) ? "lease failed" : Reason!;
        var code = string.IsNullOrWhiteSpace(ErrorCode) ? "error.resourcelease.failed" : ErrorCode!;

        IReadOnlyDictionary<string, object?>? metadata = null;
        if (Metadata is { Count: > 0 })
        {
            metadata = Metadata.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        return Error.From(message, code, cause: null!, metadata);
    }
}

public sealed record ResourceLeaseDrainRequest;

public sealed record ResourceLeaseDrainResponse(IReadOnlyList<ResourceLeasePendingItemDto> Items);

public sealed record ResourceLeaseRestoreRequest(IReadOnlyList<ResourceLeasePendingItemDto> Items);

public sealed record ResourceLeaseRestoreResponse(int RestoredCount);

public sealed record ResourceLeaseQueueStats(long PendingCount, long ActiveLeaseCount);

public sealed record ResourceLeaseAcknowledgeResponse(bool Success, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static ResourceLeaseAcknowledgeResponse Ack() => new(true);

    public static ResourceLeaseAcknowledgeResponse NotFound(string code, string message) =>
        new(false, code, message);

    public static ResourceLeaseAcknowledgeResponse FromError(Error error) =>
        new(false, error.Code, error.Message);
}

public sealed record ResourceLeasePendingItemDto(
    ResourceLeaseItemPayload Payload,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    ResourceLeaseErrorInfo? LastError,
    long SequenceId,
    ResourceLeaseOwnershipHandle? LastOwnershipToken)
{
    public static ResourceLeasePendingItemDto FromPending(TaskQueuePendingItem<ResourceLeaseWorkItem> pending)
    {
        var payload = pending.Value.ToPayload();
        var handle = pending.LastOwnershipToken.HasValue
            ? ResourceLeaseOwnershipHandle.FromToken(pending.LastOwnershipToken.Value)
            : null;

        return new ResourceLeasePendingItemDto(
            payload,
            pending.Attempt,
            pending.EnqueuedAt,
            ResourceLeaseErrorInfo.FromError(pending.LastError),
            pending.SequenceId,
            handle);
    }

    public Result<TaskQueuePendingItem<ResourceLeaseWorkItem>> ToPending()
    {
        var workItemResult = ResourceLeaseWorkItem.FromPayload(Payload);
        if (workItemResult.IsFailure)
        {
            return Err<TaskQueuePendingItem<ResourceLeaseWorkItem>>(workItemResult.Error!);
        }

        var workItem = workItemResult.Value;
        var lastToken = LastOwnershipToken?.ToToken();
        var error = LastError?.ToError() ?? Error.Unspecified("restored pending item");

        return Ok(new TaskQueuePendingItem<ResourceLeaseWorkItem>(
            workItem,
            Attempt,
            EnqueuedAt,
            error,
            SequenceId,
            lastToken));
    }
}

public sealed record ResourceLeaseOwnershipHandle(long SequenceId, int Attempt, Guid LeaseId)
{
    public TaskQueueOwnershipToken ToToken() => new(SequenceId, Attempt, LeaseId);

    public static ResourceLeaseOwnershipHandle FromToken(TaskQueueOwnershipToken token) =>
        new(token.SequenceId, token.Attempt, token.LeaseId);
}

public sealed record ResourceLeaseErrorInfo(string Message, string? Code)
{
    public static ResourceLeaseErrorInfo? FromError(Error? error)
    {
        if (error is null)
        {
            return null;
        }

        return new ResourceLeaseErrorInfo(error.Message, error.Code);
    }

    public Error ToError()
    {
        var code = string.IsNullOrWhiteSpace(Code) ? "error.resourcelease.pending" : Code!;
        return Error.From(Message, code, cause: null!, metadata: (IReadOnlyDictionary<string, object?>?)null);
    }
}

public sealed record ResourceLeaseWorkItem(
    string ResourceType,
    string ResourceId,
    string PartitionKey,
    string PayloadEncoding,
    byte[] Body,
    ImmutableDictionary<string, string> Attributes,
    string? RequestId)
{
    public static ResourceLeaseWorkItem FromPayloadOrThrow(ResourceLeaseItemPayload? payload)
    {
        var result = FromPayload(payload);
        return result.IsSuccess ? result.Value : throw new ResultException(result.Error!);
    }

    public static Result<ResourceLeaseWorkItem> FromPayload(ResourceLeaseItemPayload? payload)
    {
        if (payload is null)
        {
            return Err<ResourceLeaseWorkItem>(ResourceLeaseErrors.PayloadRequired());
        }

        if (string.IsNullOrWhiteSpace(payload.ResourceType))
        {
            return Err<ResourceLeaseWorkItem>(ResourceLeaseErrors.ResourceTypeRequired());
        }

        if (string.IsNullOrWhiteSpace(payload.ResourceId))
        {
            return Err<ResourceLeaseWorkItem>(ResourceLeaseErrors.ResourceIdRequired());
        }

        if (string.IsNullOrWhiteSpace(payload.PartitionKey))
        {
            return Err<ResourceLeaseWorkItem>(ResourceLeaseErrors.PartitionKeyRequired());
        }

        if (string.IsNullOrWhiteSpace(payload.PayloadEncoding))
        {
            return Err<ResourceLeaseWorkItem>(ResourceLeaseErrors.EncodingRequired());
        }

        var attributes = payload.Attributes is null
            ? []
            : payload.Attributes.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var body = payload.Body ?? [];

        return Ok(new ResourceLeaseWorkItem(
            payload.ResourceType,
            payload.ResourceId,
            payload.PartitionKey,
            payload.PayloadEncoding,
            body,
            attributes,
            payload.RequestId));
    }

    public ResourceLeaseItemPayload ToPayload()
    {
        var attributes = Attributes.Count == 0
            ? []
            : Attributes;

        return new ResourceLeaseItemPayload(
            ResourceType,
            ResourceId,
            PartitionKey,
            PayloadEncoding,
            Body,
            attributes,
            RequestId);
    }
}
