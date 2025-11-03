using System;

namespace Polymer.Core.Peers;

public readonly struct PeerStatus(PeerState state, int inflight, DateTimeOffset? lastSuccess, DateTimeOffset? lastFailure)
{
    public PeerState State { get; } = state;

    public int Inflight { get; } = inflight;

    public DateTimeOffset? LastSuccess { get; } = lastSuccess;

    public DateTimeOffset? LastFailure { get; } = lastFailure;

    public static PeerStatus Unknown => new(PeerState.Unknown, 0, null, null);
}

public enum PeerState
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}
