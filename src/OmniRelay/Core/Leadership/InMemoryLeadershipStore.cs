using System.Collections.Concurrent;

namespace OmniRelay.Core.Leadership;

/// <summary>Default leadership store backed by process memory. Useful for dev clusters and unit tests.</summary>
public sealed class InMemoryLeadershipStore : ILeadershipStore
{
    private readonly ConcurrentDictionary<string, ScopeLeaseState> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public InMemoryLeadershipStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<LeadershipLeaseRecord?> GetAsync(string scope, CancellationToken cancellationToken)
    {
        if (_scopes.TryGetValue(scope, out var state))
        {
            lock (state.SyncRoot)
            {
                return ValueTask.FromResult(state.Lease);
            }
        }

        return ValueTask.FromResult<LeadershipLeaseRecord?>(null);
    }

    public ValueTask<LeadershipLeaseResult> TryAcquireAsync(string scope, string candidateId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _scopes.GetOrAdd(scope, static _ => new ScopeLeaseState());

        lock (state.SyncRoot)
        {
            var now = _timeProvider.GetUtcNow();
            if (state.Lease is { } lease && !lease.IsExpired(now))
            {
                return ValueTask.FromResult(LeadershipLeaseResult.Failure(LeadershipLeaseFailureReason.LeaseHeld, lease));
            }

            var term = state.LastTerm + 1;
            var fence = state.LastFence + 1;
            var issuedAt = now;
            var record = new LeadershipLeaseRecord(scope, candidateId, term, fence, issuedAt, issuedAt.Add(leaseDuration));
            state.Lease = record;
            state.LastTerm = term;
            state.LastFence = fence;
            return ValueTask.FromResult(LeadershipLeaseResult.Success(record));
        }
    }

    public ValueTask<LeadershipLeaseResult> TryRenewAsync(string scope, LeadershipLeaseRecord lease, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_scopes.TryGetValue(scope, out var state))
        {
            return ValueTask.FromResult(LeadershipLeaseResult.Failure(LeadershipLeaseFailureReason.NotCurrentLeader));
        }

        lock (state.SyncRoot)
        {
            if (state.Lease is not { } current || current.FenceToken != lease.FenceToken || !string.Equals(current.LeaderId, lease.LeaderId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(LeadershipLeaseResult.Failure(LeadershipLeaseFailureReason.NotCurrentLeader, state.Lease));
            }

            var now = _timeProvider.GetUtcNow();
            if (current.IsExpired(now))
            {
                state.Lease = null;
                return ValueTask.FromResult(LeadershipLeaseResult.Failure(LeadershipLeaseFailureReason.LeaseExpired));
            }

            var renewed = current with
            {
                IssuedAt = now,
                ExpiresAt = now.Add(leaseDuration)
            };

            state.Lease = renewed;
            return ValueTask.FromResult(LeadershipLeaseResult.Success(renewed));
        }
    }

    public ValueTask<bool> TryReleaseAsync(string scope, LeadershipLeaseRecord lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_scopes.TryGetValue(scope, out var state))
        {
            return ValueTask.FromResult(false);
        }

        lock (state.SyncRoot)
        {
            if (state.Lease is { } current && current.FenceToken == lease.FenceToken && string.Equals(current.LeaderId, lease.LeaderId, StringComparison.Ordinal))
            {
                state.Lease = null;
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }
    }

    private sealed class ScopeLeaseState
    {
        public object SyncRoot { get; } = new();

        public LeadershipLeaseRecord? Lease;

        public long LastFence;

        public long LastTerm;
    }
}
