using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;

namespace OmniRelay.Core.Leadership;

/// <summary>Background coordinator that drives leadership elections per configured scope.</summary>
public sealed partial class LeadershipCoordinator : ILifecycle, ILeadershipObserver, IDisposable
{
    private readonly LeadershipOptions _options;
    private readonly ILeadershipStore _store;
    private readonly IMeshGossipAgent _gossipAgent;
    private readonly PeerLeaseHealthTracker? _leaseHealthTracker;
    private readonly LeadershipEventHub _eventHub;
    private readonly ILogger<LeadershipCoordinator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ScopeState> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public LeadershipCoordinator(
        LeadershipOptions options,
        ILeadershipStore leadershipStore,
        IMeshGossipAgent gossipAgent,
        LeadershipEventHub eventHub,
        ILogger<LeadershipCoordinator> logger,
        PeerLeaseHealthTracker? leaseHealthTracker = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = leadershipStore ?? throw new ArgumentNullException(nameof(leadershipStore));
        _gossipAgent = gossipAgent ?? throw new ArgumentNullException(nameof(gossipAgent));
        _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _leaseHealthTracker = leaseHealthTracker;
        _timeProvider = timeProvider ?? TimeProvider.System;

        NodeId = ResolveNodeId();

        foreach (var scope in ResolveScopes())
        {
            _scopes.TryAdd(scope.ScopeId, new ScopeState(scope));
        }

        if (_scopes.IsEmpty)
        {
            LeadershipCoordinatorLog.NoScopesConfigured(_logger);
        }
    }

    /// <summary>Identifier used when acquiring leases (defaults to gossip node id).</summary>
    public string NodeId { get; }

    /// <summary>Set of scopes monitored by this coordinator.</summary>
    public IReadOnlyCollection<LeadershipScope> Scopes => _scopes.Values.Select(state => state.Scope).ToArray();

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            LeadershipCoordinatorLog.CoordinatorDisabled(_logger);
            return;
        }

        lock (_lifecycleLock)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_lifecycleLock)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        using (cts)
        {
            cts.Cancel();
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
            }
        }

        foreach (var state in _scopes.Values)
        {
            if (state.Lease is { } lease && string.Equals(lease.LeaderId, NodeId, StringComparison.Ordinal))
            {
                try
                {
                    await _store.TryReleaseAsync(state.Scope.ScopeId, lease, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LeadershipCoordinatorLog.FailedToReleaseScope(_logger, state.Scope.ScopeId, ex);
                }

                PublishLoss(state, lease, LeadershipEventKind.SteppedDown, "shutdown");
                state.Lease = null;
                state.LastFailure = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public LeadershipSnapshot Snapshot() => _eventHub.Snapshot();

    public LeadershipToken? GetToken(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        return Snapshot().Find(scope);
    }

    public IAsyncEnumerable<LeadershipEvent> SubscribeAsync(string? scopeFilter, CancellationToken cancellationToken) =>
        _eventHub.SubscribeAsync(scopeFilter, cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MeshGossipClusterView? snapshot = null;
            if (_gossipAgent.IsEnabled)
            {
                snapshot = _gossipAgent.Snapshot();
            }

            foreach (var state in _scopes.Values)
            {
                try
                {
                    await EvaluateScopeAsync(state, snapshot, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LeadershipCoordinatorLog.EvaluationFailed(_logger, state.Scope.ScopeId, ex);
                }
            }

            try
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(25, 125));
                await Task.Delay(_options.EvaluationInterval + jitter, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async ValueTask EvaluateScopeAsync(ScopeState state, MeshGossipClusterView? gossipView, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        if (state.Lease is { } lease && lease.IsExpired(now))
        {
            PublishLoss(state, lease, LeadershipEventKind.Expired, "lease expired");
            state.Lease = null;
            state.LastFailure = null;
        }

        var isLocalEligible = IsLocalEligible(gossipView);

        if (state.Lease is null)
        {
            if (!isLocalEligible)
            {
                await ObserveStoreAsync(state, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (state.LastFailure is { } failure && now - failure < _options.ElectionBackoff)
            {
                await ObserveStoreAsync(state, cancellationToken).ConfigureAwait(false);
                return;
            }

            await TryAcquireAsync(state, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(state.Lease.LeaderId, NodeId, StringComparison.Ordinal))
        {
            var remaining = state.Lease.ExpiresAt - now;
            if (remaining <= _options.RenewalLeadTime)
            {
                await TryRenewAsync(state, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (gossipView is not null && !IsLeaderHealthy(state.Lease.LeaderId, gossipView))
        {
            LeadershipMetrics.RecordSplitBrain(state.Scope.ScopeId, state.Lease.LeaderId);
            LeadershipCoordinatorLog.SplitBrainDetected(_logger, state.Scope.ScopeId, state.Lease.LeaderId);
        }

        if (!string.Equals(state.Lease.LeaderId, NodeId, StringComparison.Ordinal))
        {
            PublishObservation(state, state.Lease, "incumbent healthy");
            await ObserveStoreAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ObserveStoreAsync(ScopeState state, CancellationToken cancellationToken)
    {
        var lease = await _store.GetAsync(state.Scope.ScopeId, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            if (state.Lease is { } previous && !string.Equals(previous.LeaderId, NodeId, StringComparison.Ordinal))
            {
                PublishObservedLoss(state, previous, "observed release");
            }

            state.Lease = null;
            state.LastFailure = null;
            return;
        }

        state.Lease = lease;

        if (!string.Equals(lease.LeaderId, NodeId, StringComparison.Ordinal))
        {
            PublishObservation(state, lease, "observed incumbent");
        }
    }

    private async ValueTask TryAcquireAsync(ScopeState state, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.LastElectionStart = startedAt;
        var correlationId = Guid.NewGuid();

        var result = await _store.TryAcquireAsync(state.Scope.ScopeId, NodeId, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && result.Lease is { } lease)
        {
            state.Lease = lease;
            state.LastFailure = null;
            PublishAcquired(state, lease, LeadershipEventKind.Elected, correlationId, startedAt);
            return;
        }

        state.LastFailure = startedAt;
        if (result.Lease is { } incumbent)
        {
            state.Lease = incumbent;
            PublishObservation(state, incumbent, "observed incumbent");
        }

        LeadershipCoordinatorLog.AcquisitionDeclined(
            _logger,
            state.Scope.ScopeId,
            result.FailureReason?.ToString() ?? "unknown",
            state.Lease?.LeaderId ?? "(none)");
    }

    private async ValueTask TryRenewAsync(ScopeState state, CancellationToken cancellationToken)
    {
        if (state.Lease is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = Guid.NewGuid();
        var result = await _store.TryRenewAsync(state.Scope.ScopeId, state.Lease, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded && result.Lease is { } renewed)
        {
            state.Lease = renewed;
            PublishEvent(state, renewed, LeadershipEventKind.Renewed, "lease renewed", correlationId);
            return;
        }

        if (result.FailureReason == LeadershipLeaseFailureReason.LeaseExpired && state.Lease is { } expired)
        {
            PublishLoss(state, expired, LeadershipEventKind.Expired, "lease expired during renew");
            state.Lease = null;
            state.LastFailure = null;
            return;
        }

        if (result.Lease is { } updated)
        {
            PublishLoss(state, state.Lease, LeadershipEventKind.Lost, "lease revoked");
            state.Lease = updated;
            if (!string.Equals(updated.LeaderId, NodeId, StringComparison.Ordinal))
            {
                PublishObservation(state, updated, "observed incumbent");
            }
        }
        else
        {
            PublishLoss(state, state.Lease, LeadershipEventKind.Lost, "lease revoked");
            state.Lease = null;
            state.LastFailure = null;
        }
    }

    private void PublishAcquired(ScopeState state, LeadershipLeaseRecord lease, LeadershipEventKind kind, Guid correlationId, DateTimeOffset startedAt)
    {
        PublishEvent(state, lease, kind, "acquired", correlationId);

        if (state.LastElectionStart is { } electionStart)
        {
            var duration = (_timeProvider.GetUtcNow() - electionStart).TotalMilliseconds;
            LeadershipMetrics.RecordElectionDuration(state.Scope.ScopeId, state.Scope.ScopeKind, duration);
            if (duration > _options.MaxElectionWindow.TotalMilliseconds)
            {
                LeadershipCoordinatorLog.ElectionExceededSla(
                    _logger,
                    state.Scope.ScopeId,
                    duration,
                    _options.MaxElectionWindow.TotalMilliseconds);
            }
        }
        else
        {
            var duration = (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds;
            LeadershipMetrics.RecordElectionDuration(state.Scope.ScopeId, state.Scope.ScopeKind, duration);
        }
    }

    private void PublishEvent(ScopeState state, LeadershipLeaseRecord lease, LeadershipEventKind kind, string reason, Guid correlationId)
    {
        var token = LeadershipToken.FromLease(state.Scope, lease);
        if (kind is LeadershipEventKind.Elected or LeadershipEventKind.Renewed)
        {
            _eventHub.UpsertToken(token);
        }

        var leadershipEvent = new LeadershipEvent(
            kind,
            state.Scope.ScopeId,
            lease.LeaderId,
            token,
            reason,
            correlationId,
            _timeProvider.GetUtcNow());

        _eventHub.Publish(leadershipEvent);
        LeadershipMetrics.RecordTransition(state.Scope.ScopeId, state.Scope.ScopeKind, kind, lease.LeaderId);

        state.LastPublishedFence = lease.FenceToken;
        state.LastPublishedLeader = lease.LeaderId;

        if (string.Equals(lease.LeaderId, NodeId, StringComparison.Ordinal))
        {
            UpdateLeaseHealth(state.Scope.ScopeId, true);
        }
    }

    private void PublishLoss(ScopeState state, LeadershipLeaseRecord lease, LeadershipEventKind kind, string reason)
    {
        var token = LeadershipToken.FromLease(state.Scope, lease);
        if (kind is LeadershipEventKind.Lost or LeadershipEventKind.Expired or LeadershipEventKind.SteppedDown)
        {
            _eventHub.RemoveScope(state.Scope.ScopeId);
        }

        var leadershipEvent = new LeadershipEvent(
            kind,
            state.Scope.ScopeId,
            lease.LeaderId,
            token,
            reason,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow());

        _eventHub.Publish(leadershipEvent);
        LeadershipMetrics.RecordTransition(state.Scope.ScopeId, state.Scope.ScopeKind, kind, lease.LeaderId);

        state.LastPublishedFence = 0;
        state.LastPublishedLeader = null;

        if (string.Equals(lease.LeaderId, NodeId, StringComparison.Ordinal))
        {
            UpdateLeaseHealth(state.Scope.ScopeId, false);
        }
    }

    private void PublishObservation(ScopeState state, LeadershipLeaseRecord lease, string reason)
    {
        if (state.LastPublishedFence == lease.FenceToken && string.Equals(state.LastPublishedLeader, lease.LeaderId, StringComparison.Ordinal))
        {
            return;
        }

        state.LastPublishedFence = lease.FenceToken;
        state.LastPublishedLeader = lease.LeaderId;

        var token = LeadershipToken.FromLease(state.Scope, lease);
        _eventHub.UpsertToken(token);

        var leadershipEvent = new LeadershipEvent(
            LeadershipEventKind.Observed,
            state.Scope.ScopeId,
            lease.LeaderId,
            token,
            reason,
            Guid.Empty,
            _timeProvider.GetUtcNow());

        _eventHub.Publish(leadershipEvent);
    }

    private void PublishObservedLoss(ScopeState state, LeadershipLeaseRecord lease, string reason)
    {
        state.LastPublishedFence = 0;
        state.LastPublishedLeader = null;
        _eventHub.RemoveScope(state.Scope.ScopeId);

        var token = LeadershipToken.FromLease(state.Scope, lease);
        var leadershipEvent = new LeadershipEvent(
            LeadershipEventKind.Observed,
            state.Scope.ScopeId,
            lease.LeaderId,
            token,
            reason,
            Guid.Empty,
            _timeProvider.GetUtcNow());

        _eventHub.Publish(leadershipEvent);
    }

    private bool IsLocalEligible(MeshGossipClusterView? snapshot)
    {
        if (!_gossipAgent.IsEnabled)
        {
            return true;
        }

        if (snapshot is null)
        {
            return false;
        }

        var local = snapshot.Members.FirstOrDefault(member => string.Equals(member.NodeId, NodeId, StringComparison.Ordinal));
        return local is not null && local.Status == MeshGossipMemberStatus.Alive;
    }

    private static bool IsLeaderHealthy(string leaderId, MeshGossipClusterView snapshot)
    {
        var member = snapshot.Members.FirstOrDefault(m => string.Equals(m.NodeId, leaderId, StringComparison.Ordinal));
        return member is not null && member.Status == MeshGossipMemberStatus.Alive;
    }

    private string ResolveNodeId()
    {
        if (!string.IsNullOrWhiteSpace(_options.NodeId))
        {
            return _options.NodeId!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_gossipAgent.LocalMetadata.NodeId))
        {
            return _gossipAgent.LocalMetadata.NodeId;
        }

        return $"mesh-{Environment.MachineName}";
    }

    private IEnumerable<LeadershipScope> ResolveScopes()
    {
        var descriptors = new List<LeadershipScopeDescriptor>();
        if (_options.Scopes.Count == 0 && _options.Shards.Count == 0)
        {
            descriptors.Add(LeadershipScopeDescriptor.GlobalControl());
        }
        else
        {
            descriptors.AddRange(_options.Scopes);
            foreach (var shard in _options.Shards)
            {
                if (string.IsNullOrWhiteSpace(shard.Namespace))
                {
                    continue;
                }

                foreach (var shardId in shard.Shards.Where(static shardId => !string.IsNullOrWhiteSpace(shardId)))
                {
                    descriptors.Add(new LeadershipScopeDescriptor
                    {
                        Namespace = shard.Namespace,
                        ShardId = shardId,
                        Kind = LeadershipScopeKinds.Shard
                    });
                }
            }
        }

        foreach (var descriptor in descriptors)
        {
            LeadershipScope scope;
            try
            {
                scope = descriptor.ToScope();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LeadershipCoordinatorLog.InvalidScopeConfiguration(_logger, ex);
                continue;
            }

            yield return scope;
        }
    }

    private void UpdateLeaseHealth(string scopeId, bool isLeader)
    {
        if (_leaseHealthTracker is null)
        {
            return;
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"mesh.leader.scope.{scopeId}"] = isLeader ? "leader" : "follower"
        };

        _leaseHealthTracker.RecordGossip(NodeId, metadata);
    }

    private sealed class ScopeState
    {
        public ScopeState(LeadershipScope scope)
        {
            Scope = scope;
        }

        public LeadershipScope Scope { get; }

        public LeadershipLeaseRecord? Lease;

        public DateTimeOffset? LastFailure;

        public DateTimeOffset? LastElectionStart;

        public long LastPublishedFence;

        public string? LastPublishedLeader;
    }

    private static partial class LeadershipCoordinatorLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Leadership coordinator started with zero scopes. Register scopes via configuration to enable elections.")]
        public static partial void NoScopesConfigured(ILogger logger);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Leadership coordinator disabled via configuration; skipping startup.")]
        public static partial void CoordinatorDisabled(ILogger logger);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed to release leadership scope {Scope} during shutdown.")]
        public static partial void FailedToReleaseScope(ILogger logger, string scope, Exception exception);

        [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Leadership evaluation failed for scope {Scope}.")]
        public static partial void EvaluationFailed(ILogger logger, string scope, Exception exception);

        [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Detected potential split-brain for scope {Scope}. Incumbent {Leader} is missing or unhealthy in gossip view.")]
        public static partial void SplitBrainDetected(ILogger logger, string scope, string leader);

        [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Leadership acquisition for scope {Scope} declined (reason={Reason}, incumbent={Leader}).")]
        public static partial void AcquisitionDeclined(ILogger logger, string scope, string reason, string leader);

        [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Election for scope {Scope} exceeded SLA ({Duration} ms > {Window} ms).")]
        public static partial void ElectionExceededSla(ILogger logger, string scope, double duration, double window);

        [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Skipping invalid leadership scope configuration.")]
        public static partial void InvalidScopeConfiguration(ILogger logger, Exception exception);
    }
}

/// <summary>Public projection interface consumed by control-plane endpoints.</summary>
public interface ILeadershipObserver
{
    LeadershipSnapshot Snapshot();

    LeadershipToken? GetToken(string scope);

    IAsyncEnumerable<LeadershipEvent> SubscribeAsync(string? scopeFilter, CancellationToken cancellationToken);

    IReadOnlyCollection<LeadershipScope> Scopes { get; }
}
