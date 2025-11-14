using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;

namespace OmniRelay.ControlPlane.Events;

/// <summary>Base envelope for control-plane lifecycle events.</summary>
public abstract record ControlPlaneEvent(
    string EventType,
    string ClusterId,
    string? Role,
    string? Region,
    string? Scope,
    DateTimeOffset Timestamp);

/// <summary>Event emitted whenever gossip membership is updated.</summary>
public sealed record GossipMembershipEvent(
    string ClusterId,
    string Role,
    string Region,
    string Reason,
    MeshGossipClusterView Snapshot,
    MeshGossipMemberSnapshot? ChangedMember)
    : ControlPlaneEvent(
        EventType: "gossip.membership",
        ClusterId,
        Role,
        Region,
        Scope: Snapshot.LocalNodeId,
        Timestamp: Snapshot.GeneratedAt);

/// <summary>Event emitted when leadership transitions occur.</summary>
public sealed record LeadershipControlPlaneEvent(
    LeadershipEvent Event,
    string ClusterId,
    string? Region,
    string? Role)
    : ControlPlaneEvent(
        EventType: "leadership.transition",
        ClusterId,
        Role,
        Region,
        Scope: Event.Scope,
        Timestamp: Event.OccurredAt);

/// <summary>Summarizes health derived from transport diagnostics.</summary>
public sealed record TransportHealthEvent(
    string ClusterId,
    string? Role,
    string? Region,
    string Transport,
    string Endpoint,
    bool Healthy,
    double? RoundTripTimeMs,
    string Source)
    : ControlPlaneEvent(
        EventType: "transport.health",
        ClusterId,
        Role,
        Region,
        Scope: Transport,
        Timestamp: DateTimeOffset.UtcNow);
