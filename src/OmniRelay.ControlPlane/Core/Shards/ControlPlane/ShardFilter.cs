namespace OmniRelay.Core.Shards.ControlPlane;

public sealed class ShardFilter
{
    private readonly string? _namespace;
    private readonly string? _owner;
    private readonly string? _search;
    private readonly ShardStatus[] _statuses;

    public ShardFilter(
        string? namespaceId,
        string? ownerNodeId,
        string? searchShardId,
        IEnumerable<ShardStatus>? statuses)
    {
        _namespace = string.IsNullOrWhiteSpace(namespaceId) ? null : namespaceId.Trim();
        _owner = string.IsNullOrWhiteSpace(ownerNodeId) ? null : ownerNodeId.Trim();
        _search = string.IsNullOrWhiteSpace(searchShardId) ? null : searchShardId.Trim();
        _statuses = statuses?.ToArray() ?? Array.Empty<ShardStatus>();
    }

    public ShardQueryOptions ToQueryOptions(int? pageSize, ShardQueryCursor? cursor)
    {
        return new ShardQueryOptions
        {
            Namespace = _namespace,
            OwnerNodeId = _owner,
            SearchShardId = _search,
            Statuses = _statuses,
            PageSize = pageSize.GetValueOrDefault(ShardQueryOptions.DefaultPageSize),
            Cursor = cursor
        };
    }

    public bool Matches(ShardRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_namespace is not null &&
            !string.Equals(_namespace, record.Namespace, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_owner is not null &&
            !string.Equals(_owner, record.OwnerNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_statuses.Length > 0 && Array.IndexOf(_statuses, record.Status) < 0)
        {
            return false;
        }

        if (_search is not null &&
            record.ShardId?.Contains(_search, StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        return true;
    }
}
