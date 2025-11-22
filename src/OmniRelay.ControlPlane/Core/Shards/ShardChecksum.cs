using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace OmniRelay.Core.Shards;

/// <summary>Computes deterministic checksums for shard records used by caches and optimistic concurrency.</summary>
public static class ShardChecksum
{
    public static string FromRecord(ShardRecord record) =>
        Compute(record.Namespace, record.ShardId, record.StrategyId, record.OwnerNodeId, record.LeaderId, record.CapacityHint, record.Status, record.ChangeTicket);

    public static string FromMutation(ShardMutationRequest mutation) =>
        Compute(mutation.Namespace, mutation.ShardId, mutation.StrategyId, mutation.OwnerNodeId, mutation.LeaderId, mutation.CapacityHint, mutation.Status, mutation.ChangeTicket);

    public static string Compute(
        string @namespace,
        string shardId,
        string strategyId,
        string ownerNodeId,
        string? leaderId,
        double capacityHint,
        ShardStatus status,
        string? changeTicket)
    {
        var builder = new StringBuilder(@namespace.Length + shardId.Length + strategyId.Length + ownerNodeId.Length + 64);
        builder.Append(@namespace).Append('|');
        builder.Append(shardId).Append('|');
        builder.Append(strategyId).Append('|');
        builder.Append(ownerNodeId).Append('|');
        builder.Append(leaderId ?? string.Empty).Append('|');
        builder.Append(capacityHint.ToString(CultureInfo.InvariantCulture)).Append('|');
        builder.Append((int)status).Append('|');
        builder.Append(changeTicket ?? string.Empty);

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash);
    }
}
