using Hugo;

namespace OmniRelay.Core.Shards.ControlPlane;

internal static class ShardControlPlaneErrors
{
    private const string FilterRequiredCode = "shards.control.filter.required";
    private const string CursorInvalidCode = "shards.control.cursor.invalid";
    private const string NamespaceRequiredCode = "shards.control.namespace.required";
    private const string NamespaceMissingRecordsCode = "shards.control.namespace.missing";
    private const string NodesRequiredCode = "shards.control.nodes.required";
    private const string NodeIdInvalidCode = "shards.control.node_id.invalid";
    private const string RepositoryFailureCode = "shards.control.repository.failure";
    private const string StreamFailureCode = "shards.control.stream.failure";
    private const string SimulationRequestRequiredCode = "shards.control.simulation.request_required";
    private const string AssignmentMissingCode = "shards.control.assignment.missing";
    private const string AssignmentFailedCode = "shards.control.assignment.failed";

    public static Error FilterRequired() =>
        Error.From("A shard filter must be provided.", FilterRequiredCode);

    public static Error InvalidCursor(string? cursorToken) =>
        Error.From("Invalid cursor token.", CursorInvalidCode)
            .WithMetadata("cursor", cursorToken ?? string.Empty);

    public static Error NamespaceRequired() =>
        Error.From("Namespace must be provided.", NamespaceRequiredCode);

    public static Error NamespaceMissing(string @namespace) =>
        Error.From($"Namespace '{@namespace}' does not contain shard records.", NamespaceMissingRecordsCode)
            .WithMetadata("namespace", @namespace);

    public static Error NodesRequired() =>
        Error.From("At least one node must be provided.", NodesRequiredCode);

    public static Error NodeIdInvalid(string? nodeId) =>
        Error.From("Node id cannot be empty.", NodeIdInvalidCode)
            .WithMetadata("nodeId", nodeId ?? string.Empty);

    public static Error RepositoryFailure(Exception exception, string stage) =>
        Error.FromException(exception)
            .WithCode(RepositoryFailureCode)
            .WithMetadata("stage", stage);

    public static Error StreamFailure(Exception exception, string stage) =>
        Error.FromException(exception)
            .WithCode(StreamFailureCode)
            .WithMetadata("stage", stage);

    public static Error SimulationRequestRequired() =>
        Error.From("Simulation request body is required.", SimulationRequestRequiredCode);

    public static Error AssignmentMissing(string shardId) =>
        Error.From($"Shard assignment refers to missing shard '{shardId}'.", AssignmentMissingCode)
            .WithMetadata("shardId", shardId);

    public static Error AssignmentFailed(string shardId, string reason) =>
        Error.From(reason, AssignmentFailedCode)
            .WithMetadata("shardId", shardId);
}
