namespace OmniRelay.Core.Peers;

/// <summary>
/// Snapshot of a peer's state, in-flight requests, and last success/failure timestamps.
/// </summary>
public readonly struct PeerStatus(PeerState state, int inflight, DateTimeOffset? lastSuccess, DateTimeOffset? lastFailure) : IEquatable<PeerStatus>
{
    /// <summary>Gets the current peer state.</summary>
    public PeerState State { get; } = state;

    /// <summary>Gets the number of in-flight requests.</summary>
    public int Inflight { get; } = inflight;

    /// <summary>Gets the last success timestamp, if known.</summary>
    public DateTimeOffset? LastSuccess { get; } = lastSuccess;

    /// <summary>Gets the last failure timestamp, if known.</summary>
    public DateTimeOffset? LastFailure { get; } = lastFailure;

    /// <summary>Unknown status sentinel.</summary>
    public static PeerStatus Unknown => new(PeerState.Unknown, 0, null, null);

    public override bool Equals(object obj)
    {
        throw new NotImplementedException();
    }

    public override int GetHashCode()
    {
        throw new NotImplementedException();
    }

    public static bool operator ==(PeerStatus left, PeerStatus right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(PeerStatus left, PeerStatus right)
    {
        return !(left == right);
    }

    public bool Equals(PeerStatus other)
    {
        throw new NotImplementedException();
    }
}

/// <summary>Represents the connectivity state of a peer.</summary>
public enum PeerState
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}
