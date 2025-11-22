using System.Collections.Immutable;

namespace OmniRelay.ControlPlane.Upgrade;

/// <summary>Snapshot of node drain status and participant states.</summary>
public sealed record NodeDrainSnapshot(
    NodeDrainState State,
    string? Reason,
    DateTimeOffset UpdatedAt,
    ImmutableArray<NodeDrainParticipantSnapshot> Participants);

/// <summary>Snapshot for a single drain participant.</summary>
public sealed record NodeDrainParticipantSnapshot(
    string Name,
    NodeDrainParticipantState State,
    string? LastError,
    DateTimeOffset UpdatedAt);

/// <summary>Participant state for node drain orchestration.</summary>
public enum NodeDrainParticipantState
{
    Active = 0,
    Draining = 1,
    Drained = 2,
    Failed = 3
}
