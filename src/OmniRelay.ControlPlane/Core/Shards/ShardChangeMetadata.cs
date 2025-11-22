namespace OmniRelay.Core.Shards;

/// <summary>Describes the governance metadata that accompanies a shard mutation.</summary>
public readonly record struct ShardChangeMetadata
{
    public ShardChangeMetadata(string actor, string reason, string? changeTicket = null, double? ownershipDeltaPercent = null, string? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("Actor must be provided.", nameof(actor));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason must be provided.", nameof(reason));
        }

        Actor = actor;
        Reason = reason;
        ChangeTicket = changeTicket;
        OwnershipDeltaPercent = ownershipDeltaPercent;
        Metadata = metadata;
    }

    public string Actor { get; }

    public string Reason { get; }

    public string? ChangeTicket { get; }

    public double? OwnershipDeltaPercent { get; }

    public string? Metadata { get; }
}
