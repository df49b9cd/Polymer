using System.Text;

namespace OmniRelay.Core.Shards;

/// <summary>Cursor used for shard queries when paging through namespaces + shard ids.</summary>
public sealed record ShardQueryCursor(string Namespace, string ShardId)
{
    private const char Delimiter = '|';

    public static bool TryParse(string? token, out ShardQueryCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(token.Trim());
            var payload = Encoding.UTF8.GetString(bytes);
            var separatorIndex = payload.IndexOf(Delimiter);
            if (separatorIndex <= 0 || separatorIndex >= payload.Length - 1)
            {
                return false;
            }

            var ns = payload[..separatorIndex];
            var shardId = payload[(separatorIndex + 1)..];
            if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(shardId))
            {
                return false;
            }

            cursor = new ShardQueryCursor(ns, shardId);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static ShardQueryCursor FromRecord(ShardRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ShardQueryCursor(record.Namespace, record.ShardId);
    }

    public string Encode()
    {
        var payload = $"{Namespace}{Delimiter}{ShardId}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        return Convert.ToBase64String(bytes);
    }
}

/// <summary>Represents paging/filter inputs for shard listings.</summary>
public sealed class ShardQueryOptions
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    public string? Namespace { get; init; }

    public string? OwnerNodeId { get; init; }

    public string? SearchShardId { get; init; }

    public IReadOnlyList<ShardStatus> Statuses { get; init; } = Array.Empty<ShardStatus>();

    public int PageSize { get; init; } = DefaultPageSize;

    public ShardQueryCursor? Cursor { get; init; }

    public int ResolvePageSize()
    {
        if (PageSize <= 0)
        {
            return DefaultPageSize;
        }

        return Math.Min(PageSize, MaxPageSize);
    }
}

/// <summary>Result envelope for shard queries.</summary>
public sealed record ShardQueryResult(
    IReadOnlyList<ShardRecord> Items,
    ShardQueryCursor? NextCursor,
    long HighestVersion);
