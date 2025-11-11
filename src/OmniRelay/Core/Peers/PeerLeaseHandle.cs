using Hugo;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Lightweight descriptor for SafeTaskQueue ownership tokens used when gossiping lease health.
/// </summary>
public readonly record struct PeerLeaseHandle(long SequenceId, int Attempt, Guid LeaseId)
{
    /// <summary>Create a handle from a SafeTaskQueue ownership token.</summary>
    public static PeerLeaseHandle FromToken(TaskQueueOwnershipToken token) =>
        new(token.SequenceId, token.Attempt, token.LeaseId);

    /// <summary>Convert the handle back into a SafeTaskQueue ownership token.</summary>
    public TaskQueueOwnershipToken ToToken() => new(SequenceId, Attempt, LeaseId);

    public long SequenceId { get; init; } = SequenceId;

    public int Attempt { get; init; } = Attempt;

    public Guid LeaseId { get; init; } = LeaseId;
}
