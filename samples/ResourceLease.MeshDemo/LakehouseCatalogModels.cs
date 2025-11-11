using System.Collections.Concurrent;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal enum LakehouseCatalogOperationType
{
    CreateTable,
    AlterSchema,
    CommitSnapshot,
    Vacuum
}

internal sealed record LakehouseCatalogOperation(
    string Catalog,
    string Database,
    string Table,
    LakehouseCatalogOperationType OperationType,
    int Version,
    string Principal,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> Changes,
    string SnapshotId,
    DateTimeOffset Timestamp,
    string RequestId)
{
    public string ResourceId => $"{Catalog}.{Database}.{Table}.v{Version:D4}";
}

internal sealed record LakehouseCatalogTableState(
    string Catalog,
    string Database,
    string Table,
    int Version,
    LakehouseCatalogOperationType Operation,
    IReadOnlyList<string> Columns,
    string Principal,
    string SnapshotId,
    DateTimeOffset UpdatedAt);

internal sealed record LakehouseCatalogSnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<LakehouseCatalogTableState> Tables,
    IReadOnlyList<LakehouseCatalogOperation> RecentOperations);

internal enum LakehouseCatalogApplyOutcome
{
    Created,
    Updated,
    Duplicate,
    Stale
}

internal readonly record struct LakehouseCatalogApplyResult(
    LakehouseCatalogApplyOutcome Outcome,
    LakehouseCatalogTableState State);

internal sealed class LakehouseCatalogState
{
    private const int MaxRecentOperations = 128;
    private readonly ConcurrentDictionary<string, LakehouseCatalogTableState> _tables = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LakehouseCatalogOperation> _recentOperations = new();

    public LakehouseCatalogApplyResult Apply(LakehouseCatalogOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var key = CreateKey(operation.Catalog, operation.Database, operation.Table);

        while (true)
        {
            if (_tables.TryGetValue(key, out var existing))
            {
                if (operation.Version < existing.Version)
                {
                    return new LakehouseCatalogApplyResult(LakehouseCatalogApplyOutcome.Stale, existing);
                }

                if (operation.Version == existing.Version)
                {
                    RecordOperation(operation);
                    return new LakehouseCatalogApplyResult(LakehouseCatalogApplyOutcome.Duplicate, existing);
                }

                var updated = existing with
                {
                    Version = operation.Version,
                    Operation = operation.OperationType,
                    Columns = operation.Columns,
                    Principal = operation.Principal,
                    SnapshotId = operation.SnapshotId,
                    UpdatedAt = operation.Timestamp
                };

                if (_tables.TryUpdate(key, updated, existing))
                {
                    RecordOperation(operation);
                    return new LakehouseCatalogApplyResult(LakehouseCatalogApplyOutcome.Updated, updated);
                }

                continue;
            }

            var created = new LakehouseCatalogTableState(
                operation.Catalog,
                operation.Database,
                operation.Table,
                operation.Version,
                operation.OperationType,
                operation.Columns,
                operation.Principal,
                operation.SnapshotId,
                operation.Timestamp);

            if (_tables.TryAdd(key, created))
            {
                RecordOperation(operation);
                return new LakehouseCatalogApplyResult(LakehouseCatalogApplyOutcome.Created, created);
            }
        }
    }

    public LakehouseCatalogSnapshot Snapshot(int maxTables = 50, int maxOperations = 50)
    {
        var tables = _tables.Values
            .OrderByDescending(t => t.Version)
            .ThenBy(t => t.Catalog, StringComparer.Ordinal)
            .ThenBy(t => t.Database, StringComparer.Ordinal)
            .ThenBy(t => t.Table, StringComparer.Ordinal)
            .Take(Math.Max(1, maxTables))
            .ToArray();

        var recent = _recentOperations
            .Take(Math.Max(1, maxOperations))
            .ToArray();

        return new LakehouseCatalogSnapshot(DateTimeOffset.UtcNow, tables, recent);
    }

    private void RecordOperation(LakehouseCatalogOperation operation)
    {
        _recentOperations.Enqueue(operation);
        while (_recentOperations.Count > MaxRecentOperations && _recentOperations.TryDequeue(out _))
        {
        }
    }

    private static string CreateKey(string catalog, string database, string table) =>
        $"{catalog}::{database}::{table}".ToLowerInvariant();
}
