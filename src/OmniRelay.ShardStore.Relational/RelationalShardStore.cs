using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using OmniRelay.Core.Shards;

#pragma warning disable CA2007 // awaited disposals cannot use ConfigureAwait

namespace OmniRelay.ShardStore.Relational;

/// <summary>Relational database backed shard repository with optimistic concurrency and audit history.</summary>
public sealed class RelationalShardStore : IShardRepository
{
    private readonly Func<DbConnection> _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public RelationalShardStore(Func<DbConnection> connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ShardRecord?> GetAsync(ShardKey key, CancellationToken cancellationToken = default)
    {
        var connection = _connectionFactory();
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = Sql.SelectSingle;
        AddParameter(command, "@namespace", key.Namespace);
        AddParameter(command, "@shard", key.ShardId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadRecord(reader);
        }

        return null;
    }

    public async ValueTask<IReadOnlyList<ShardRecord>> ListAsync(string? namespaceId = null, CancellationToken cancellationToken = default)
    {
        var connection = _connectionFactory();
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(namespaceId))
        {
            command.CommandText = Sql.SelectAll;
        }
        else
        {
            command.CommandText = Sql.SelectByNamespace;
            AddParameter(command, "@namespace", namespaceId);
        }

        var results = new List<ShardRecord>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    public async ValueTask<ShardMutationResult> UpsertAsync(ShardMutationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = _connectionFactory();
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);

        var existing = await LoadRecordAsync(connection, transaction, request.Key, cancellationToken).ConfigureAwait(false);
        if (request.ExpectedVersion.HasValue)
        {
            var currentVersion = existing?.Version ?? 0;
            if (request.ExpectedVersion.Value != currentVersion)
            {
                throw new ShardConcurrencyException($"Shard '{request.Key}' expected version {request.ExpectedVersion.Value} but was {currentVersion}.");
            }
        }

        var nextVersion = (existing?.Version ?? 0) + 1;
        var changeTicket = request.ChangeTicket ?? request.ChangeMetadata.ChangeTicket;
        var checksum = ShardChecksum.Compute(
            request.Namespace,
            request.ShardId,
            request.StrategyId,
            request.OwnerNodeId,
            request.LeaderId,
            request.CapacityHint,
            request.Status,
            changeTicket);
        var now = _timeProvider.GetUtcNow();

        if (existing is null)
        {
            await InsertShardAsync(connection, transaction, request, checksum, changeTicket, nextVersion, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var rows = await UpdateShardAsync(connection, transaction, request, checksum, changeTicket, nextVersion, existing.Version, now, cancellationToken).ConfigureAwait(false);
            if (rows == 0)
            {
                throw new ShardConcurrencyException($"Shard '{request.Key}' was updated by another actor.");
            }
        }

        var historyRecord = await InsertHistoryAsync(connection, transaction, request, nextVersion, changeTicket, existing?.OwnerNodeId, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var record = new ShardRecord
        {
            Namespace = request.Namespace,
            ShardId = request.ShardId,
            StrategyId = request.StrategyId,
            OwnerNodeId = request.OwnerNodeId,
            LeaderId = request.LeaderId,
            CapacityHint = request.CapacityHint,
            Status = request.Status,
            Version = nextVersion,
            Checksum = checksum,
            UpdatedAt = now,
            ChangeTicket = changeTicket
        };

        return new ShardMutationResult(record, historyRecord, existing is null);
    }

    public async IAsyncEnumerable<ShardRecordDiff> StreamDiffsAsync(long? sinceVersion, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connection = _connectionFactory();
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sinceVersion.HasValue ? Sql.StreamSince : Sql.StreamAll;
        if (sinceVersion.HasValue)
        {
            AddParameter(command, "@position", sinceVersion.Value);
        }

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var position = reader.GetInt64(0);
            var record = ReadRecord(reader, 1);
            var history = ReadHistory(reader, 12);
            var previous = history.PreviousOwnerNodeId is null
                ? null
                : record with
                {
                    OwnerNodeId = history.PreviousOwnerNodeId,
                    Version = Math.Max(0, record.Version - 1),
                    Checksum = ShardChecksum.Compute(
                        record.Namespace,
                        record.ShardId,
                        record.StrategyId,
                        history.PreviousOwnerNodeId,
                        record.LeaderId,
                        record.CapacityHint,
                        record.Status,
                        history.ChangeTicket ?? record.ChangeTicket),
                    UpdatedAt = history.CreatedAt,
                    ChangeTicket = history.ChangeTicket ?? record.ChangeTicket
                };

            yield return new ShardRecordDiff(position, record, previous, history);
        }
    }

    private static async Task<ShardRecord?> LoadRecordAsync(DbConnection connection, DbTransaction transaction, ShardKey key, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql.SelectSingle;
        AddParameter(command, "@namespace", key.Namespace);
        AddParameter(command, "@shard", key.ShardId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadRecord(reader);
        }

        return null;
    }

    private static async Task InsertShardAsync(
        DbConnection connection,
        DbTransaction transaction,
        ShardMutationRequest request,
        string checksum,
        string? changeTicket,
        long version,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql.Insert;
        AddParameter(command, "@namespace", request.Namespace);
        AddParameter(command, "@shard", request.ShardId);
        AddParameter(command, "@strategy", request.StrategyId);
        AddParameter(command, "@owner", request.OwnerNodeId);
        AddParameter(command, "@leader", request.LeaderId);
        AddParameter(command, "@capacity", request.CapacityHint);
        AddParameter(command, "@status", (int)request.Status, DbType.Int32);
        AddParameter(command, "@version", version, DbType.Int64);
        AddParameter(command, "@checksum", checksum);
        AddParameter(command, "@updatedAt", timestamp.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@ticket", changeTicket);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateShardAsync(
        DbConnection connection,
        DbTransaction transaction,
        ShardMutationRequest request,
        string checksum,
        string? changeTicket,
        long version,
        long expectedVersion,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql.Update;
        AddParameter(command, "@strategy", request.StrategyId);
        AddParameter(command, "@owner", request.OwnerNodeId);
        AddParameter(command, "@leader", request.LeaderId);
        AddParameter(command, "@capacity", request.CapacityHint);
        AddParameter(command, "@status", (int)request.Status, DbType.Int32);
        AddParameter(command, "@version", version, DbType.Int64);
        AddParameter(command, "@checksum", checksum);
        AddParameter(command, "@updatedAt", timestamp.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@ticket", changeTicket);
        AddParameter(command, "@namespace", request.Namespace);
        AddParameter(command, "@shard", request.ShardId);
        AddParameter(command, "@expectedVersion", expectedVersion, DbType.Int64);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ShardHistoryRecord> InsertHistoryAsync(
        DbConnection connection,
        DbTransaction transaction,
        ShardMutationRequest request,
        long version,
        string? changeTicket,
        string? previousOwner,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql.InsertHistory;
        AddParameter(command, "@namespace", request.Namespace);
        AddParameter(command, "@shard", request.ShardId);
        AddParameter(command, "@version", version, DbType.Int64);
        AddParameter(command, "@strategy", request.StrategyId);
        AddParameter(command, "@owner", request.OwnerNodeId);
        AddParameter(command, "@previousOwner", previousOwner);
        AddParameter(command, "@actor", request.ChangeMetadata.Actor);
        AddParameter(command, "@reason", request.ChangeMetadata.Reason);
        AddParameter(command, "@ticket", changeTicket ?? request.ChangeMetadata.ChangeTicket);
        AddParameter(command, "@delta", request.ChangeMetadata.OwnershipDeltaPercent);
        AddParameter(command, "@metadata", request.ChangeMetadata.Metadata);
        AddParameter(command, "@createdAt", timestamp.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new ShardHistoryRecord
        {
            Namespace = request.Namespace,
            ShardId = request.ShardId,
            Version = version,
            StrategyId = request.StrategyId,
            Actor = request.ChangeMetadata.Actor,
            Reason = request.ChangeMetadata.Reason,
            ChangeTicket = changeTicket ?? request.ChangeMetadata.ChangeTicket,
            CreatedAt = timestamp,
            OwnerNodeId = request.OwnerNodeId,
            PreviousOwnerNodeId = previousOwner,
            OwnershipDeltaPercent = request.ChangeMetadata.OwnershipDeltaPercent,
            Metadata = request.ChangeMetadata.Metadata
        };
    }

    private static ShardRecord ReadRecord(DbDataReader reader, int offset = 0)
    {
        return new ShardRecord
        {
            Namespace = reader.GetString(offset + 0),
            ShardId = reader.GetString(offset + 1),
            StrategyId = reader.GetString(offset + 2),
            OwnerNodeId = reader.GetString(offset + 3),
            LeaderId = reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
            CapacityHint = reader.GetDouble(offset + 5),
            Status = (ShardStatus)reader.GetInt32(offset + 6),
            Version = reader.GetInt64(offset + 7),
            Checksum = reader.GetString(offset + 8),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(offset + 9), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            ChangeTicket = reader.IsDBNull(offset + 10) ? null : reader.GetString(offset + 10)
        };
    }

    private static ShardHistoryRecord ReadHistory(DbDataReader reader, int offset)
    {
        return new ShardHistoryRecord
        {
            Namespace = reader.GetString(1),
            ShardId = reader.GetString(2),
            Version = reader.GetInt64(8),
            StrategyId = reader.GetString(3),
            Actor = reader.GetString(offset + 0),
            Reason = reader.GetString(offset + 1),
            ChangeTicket = reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2),
            OwnerNodeId = reader.IsDBNull(offset + 3) ? reader.GetString(4) : reader.GetString(offset + 3),
            PreviousOwnerNodeId = reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
            OwnershipDeltaPercent = reader.IsDBNull(offset + 5) ? null : reader.GetDouble(offset + 5),
            Metadata = reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(offset + 7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        };
    }

    private static void AddParameter(DbCommand command, string name, object? value, DbType? dbType = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (dbType.HasValue)
        {
            parameter.DbType = dbType.Value;
        }

        command.Parameters.Add(parameter);
    }

    private static class Sql
    {
        public const string SelectSingle = @"SELECT namespace, shard_id, strategy_id, owner_node_id, leader_id, capacity_hint, status, version, checksum, updated_at, change_ticket FROM shard_records WHERE namespace = @namespace AND shard_id = @shard LIMIT 1";

        public const string SelectAll = @"SELECT namespace, shard_id, strategy_id, owner_node_id, leader_id, capacity_hint, status, version, checksum, updated_at, change_ticket FROM shard_records ORDER BY namespace, shard_id";

        public const string SelectByNamespace = @"SELECT namespace, shard_id, strategy_id, owner_node_id, leader_id, capacity_hint, status, version, checksum, updated_at, change_ticket FROM shard_records WHERE namespace = @namespace ORDER BY shard_id";

        public const string Insert = @"INSERT INTO shard_records (namespace, shard_id, strategy_id, owner_node_id, leader_id, capacity_hint, status, version, checksum, updated_at, change_ticket)
VALUES (@namespace, @shard, @strategy, @owner, @leader, @capacity, @status, @version, @checksum, @updatedAt, @ticket)";

        public const string Update = @"UPDATE shard_records SET strategy_id = @strategy, owner_node_id = @owner, leader_id = @leader, capacity_hint = @capacity, status = @status, version = @version, checksum = @checksum, updated_at = @updatedAt, change_ticket = @ticket WHERE namespace = @namespace AND shard_id = @shard AND version = @expectedVersion";

        public const string InsertHistory = @"INSERT INTO shard_history (namespace, shard_id, version, strategy_id, owner_node_id, previous_owner_node_id, actor, reason, change_ticket, ownership_delta_percent, metadata, created_at)
VALUES (@namespace, @shard, @version, @strategy, @owner, @previousOwner, @actor, @reason, @ticket, @delta, @metadata, @createdAt)";

        public const string StreamAll = @"SELECT h.id, h.namespace, h.shard_id, h.strategy_id, h.owner_node_id, r.leader_id, r.capacity_hint, r.status, h.version, r.checksum, h.created_at, COALESCE(h.change_ticket, r.change_ticket), h.actor, h.reason, h.change_ticket, h.owner_node_id, h.previous_owner_node_id, h.ownership_delta_percent, h.metadata, h.created_at FROM shard_history h INNER JOIN shard_records r ON r.namespace = h.namespace AND r.shard_id = h.shard_id ORDER BY h.id";

        public const string StreamSince = @"SELECT h.id, h.namespace, h.shard_id, h.strategy_id, h.owner_node_id, r.leader_id, r.capacity_hint, r.status, h.version, r.checksum, h.created_at, COALESCE(h.change_ticket, r.change_ticket), h.actor, h.reason, h.change_ticket, h.owner_node_id, h.previous_owner_node_id, h.ownership_delta_percent, h.metadata, h.created_at FROM shard_history h INNER JOIN shard_records r ON r.namespace = h.namespace AND r.shard_id = h.shard_id WHERE h.id > @position ORDER BY h.id";
    }
}

#pragma warning restore CA2007
