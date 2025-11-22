using OmniRelay.Core.Shards.Hashing;

namespace OmniRelay.Core.Shards.ControlPlane;

/// <summary>Lightweight DTO describing a shard for API responses.</summary>
public sealed record ShardSummary(
    string Namespace,
    string ShardId,
    string StrategyId,
    string OwnerNodeId,
    string? LeaderId,
    double CapacityHint,
    ShardStatus Status,
    long Version,
    string Checksum,
    DateTimeOffset UpdatedAt,
    string? ChangeTicket);

public sealed record ShardListResponse(
    IReadOnlyList<ShardSummary> Items,
    string? NextCursor,
    long Version);

public sealed record ShardDiffEntry(
    long Position,
    ShardSummary Current,
    ShardSummary? Previous,
    ShardHistoryRecord? History);

public sealed record ShardDiffResponse(
    IReadOnlyList<ShardDiffEntry> Items,
    long? LastPosition);

public sealed record ShardSimulationNode(
    string NodeId,
    double? Weight,
    string? Region,
    string? Zone);

public sealed class ShardSimulationRequest
{
    public string? Namespace { get; set; }

    public string? StrategyId { get; set; }

    public IReadOnlyList<ShardSimulationNode> Nodes { get; set; } = Array.Empty<ShardSimulationNode>();
}

public sealed record ShardSimulationAssignment(
    string Namespace,
    string ShardId,
    string OwnerNodeId,
    double Capacity,
    string? LocalityHint);

public sealed record ShardSimulationChange(
    string Namespace,
    string ShardId,
    string CurrentOwner,
    string ProposedOwner,
    bool ChangesOwner);

public sealed record ShardSimulationResponse(
    string Namespace,
    string StrategyId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ShardSimulationAssignment> Assignments,
    IReadOnlyList<ShardSimulationChange> Changes);

public static class ShardControlPlaneMapper
{
    public static ShardSummary ToSummary(ShardRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ShardSummary(
            record.Namespace,
            record.ShardId,
            record.StrategyId,
            record.OwnerNodeId,
            record.LeaderId,
            record.CapacityHint,
            record.Status,
            record.Version,
            record.Checksum,
            record.UpdatedAt,
            record.ChangeTicket);
    }

    public static ShardSimulationAssignment ToAssignment(ShardAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return new ShardSimulationAssignment(
            assignment.Namespace,
            assignment.ShardId,
            assignment.OwnerNodeId,
            assignment.Capacity,
            assignment.LocalityHint);
    }

    public static ShardSimulationChange ToChange(ShardAssignment assignment, ShardRecord record)
    {
        var proposedOwner = assignment.OwnerNodeId;
        var currentOwner = record.OwnerNodeId;
        return new ShardSimulationChange(
            assignment.Namespace,
            assignment.ShardId,
            currentOwner,
            proposedOwner,
            !string.Equals(currentOwner, proposedOwner, StringComparison.Ordinal));
    }
}
