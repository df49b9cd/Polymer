using System.Collections.Immutable;

namespace OmniRelay.Core.Leadership;

/// <summary>Immutable token proving leadership for a scope until the fence expires.</summary>
public sealed record LeadershipToken(
    string Scope,
    string ScopeKind,
    string LeaderId,
    long Term,
    long FenceToken,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    ImmutableDictionary<string, string> Labels)
{
    /// <summary>Indicates whether the token has reached its expiry.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    internal static LeadershipToken FromLease(LeadershipScope scope, LeadershipLeaseRecord lease)
    {
        var labels = scope.Labels;
        return new LeadershipToken(
            scope.ScopeId,
            scope.ScopeKind,
            lease.LeaderId,
            lease.Term,
            lease.FenceToken,
            lease.IssuedAt,
            lease.ExpiresAt,
            labels);
    }
}

/// <summary>Represents a leadership lifecycle notification (election, renewal, loss).</summary>
public sealed record LeadershipEvent(
    LeadershipEventKind EventKind,
    string Scope,
    string LeaderId,
    LeadershipToken? Token,
    string? Reason,
    Guid CorrelationId,
    DateTimeOffset OccurredAt)
{
    public static LeadershipEvent ForSnapshot(LeadershipToken token) =>
        new(LeadershipEventKind.Snapshot, token.Scope, token.LeaderId, token, Reason: "snapshot", Guid.Empty, DateTimeOffset.UtcNow);
}

/// <summary>Event kinds surfaced via SSE/gRPC streams.</summary>
public enum LeadershipEventKind
{
    Snapshot,
    Observed,
    Elected,
    Renewed,
    Lost,
    Expired,
    SteppedDown
}

/// <summary>Represents an immutable snapshot of all known leadership tokens.</summary>
public sealed record LeadershipSnapshot(DateTimeOffset GeneratedAt, ImmutableArray<LeadershipToken> Tokens)
{
    public LeadershipToken? Find(string scope) =>
        Tokens.FirstOrDefault(token => string.Equals(token.Scope, scope, StringComparison.OrdinalIgnoreCase));
}
