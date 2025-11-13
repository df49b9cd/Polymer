namespace OmniRelay.Core.Leadership;

/// <summary>Abstraction over the persistence layer that serializes leadership leases with fencing guarantees.</summary>
public interface ILeadershipStore
{
    /// <summary>Gets the latest lease record if one exists for the scope.</summary>
    ValueTask<LeadershipLeaseRecord?> GetAsync(string scope, CancellationToken cancellationToken);

    /// <summary>Attempts to acquire leadership for the provided scope.</summary>
    ValueTask<LeadershipLeaseResult> TryAcquireAsync(string scope, string candidateId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Attempts to renew an existing lease owned by the candidate.</summary>
    ValueTask<LeadershipLeaseResult> TryRenewAsync(string scope, LeadershipLeaseRecord lease, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Releases the lease if still owned by the supplied record.</summary>
    ValueTask<bool> TryReleaseAsync(string scope, LeadershipLeaseRecord lease, CancellationToken cancellationToken);
}

/// <summary>Represents an issued lease inside the leadership store.</summary>
public sealed record LeadershipLeaseRecord(
    string Scope,
    string LeaderId,
    long Term,
    long FenceToken,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>Result returned by the leadership store when acquire/renew actions complete.</summary>
public readonly struct LeadershipLeaseResult(
    bool succeeded,
    LeadershipLeaseRecord? lease,
    LeadershipLeaseFailureReason? failureReason)
{
    public bool Succeeded { get; } = succeeded;

    public LeadershipLeaseRecord? Lease { get; } = lease;

    public LeadershipLeaseFailureReason? FailureReason { get; } = failureReason;

    public static LeadershipLeaseResult Success(LeadershipLeaseRecord lease) => new(true, lease, null);

    public static LeadershipLeaseResult Failure(LeadershipLeaseFailureReason reason, LeadershipLeaseRecord? lease = null) => new(false, lease, reason);
}

/// <summary>Enumerates acquire/renew failure modes.</summary>
public enum LeadershipLeaseFailureReason
{
    LeaseHeld,
    LeaseExpired,
    NotCurrentLeader,
    StoreUnavailable
}
