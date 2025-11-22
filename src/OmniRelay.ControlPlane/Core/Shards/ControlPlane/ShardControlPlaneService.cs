using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Shards.Hashing;

namespace OmniRelay.Core.Shards.ControlPlane;

public sealed class ShardControlPlaneService
{
    private readonly IShardRepository _repository;
    private readonly ShardHashStrategyRegistry _strategies;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ShardControlPlaneService> _logger;

    public ShardControlPlaneService(
        IShardRepository repository,
        ShardHashStrategyRegistry strategies,
        TimeProvider? timeProvider,
        ILogger<ShardControlPlaneService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<ShardListResponse> ListAsync(
        ShardFilter filter,
        string? cursorToken,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ShardQueryCursor? cursor = null;

        if (!string.IsNullOrWhiteSpace(cursorToken) &&
            !ShardQueryCursor.TryParse(cursorToken, out cursor))
        {
            throw new ArgumentException("Invalid cursor token.", nameof(cursorToken));
        }

        var options = filter.ToQueryOptions(pageSize, cursor);
        var result = await _repository.QueryAsync(options, cancellationToken).ConfigureAwait(false);
        var items = result.Items.Select(ShardControlPlaneMapper.ToSummary).ToArray();
        return new ShardListResponse(items, result.NextCursor?.Encode(), result.HighestVersion);
    }

    public async ValueTask<ShardDiffResponse> DiffAsync(
        long? fromPosition,
        long? toPosition,
        ShardFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var since = fromPosition.HasValue ? Math.Max(0, fromPosition.Value - 1) : (long?)null;
        var upperBound = toPosition ?? long.MaxValue;
        var diffs = new List<ShardDiffEntry>();

        await foreach (var diff in _repository.StreamDiffsAsync(since, cancellationToken).ConfigureAwait(false))
        {
            if (fromPosition.HasValue && diff.Position < fromPosition.Value)
            {
                continue;
            }

            if (diff.Position > upperBound)
            {
                break;
            }

            if (!filter.Matches(diff.Current))
            {
                continue;
            }

            diffs.Add(new ShardDiffEntry(
                diff.Position,
                ShardControlPlaneMapper.ToSummary(diff.Current),
                diff.Previous is null ? null : ShardControlPlaneMapper.ToSummary(diff.Previous),
                diff.History));
        }

        var last = diffs.Count > 0 ? diffs[^1].Position : (long?)null;
        return new ShardDiffResponse(diffs, last);
    }

    public async IAsyncEnumerable<ShardRecordDiff> WatchAsync(
        long? resumeToken,
        ShardFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await foreach (var diff in _repository.StreamDiffsAsync(resumeToken, cancellationToken).ConfigureAwait(false))
        {
            if (!filter.Matches(diff.Current))
            {
                continue;
            }

            yield return diff;
        }
    }

    public async ValueTask<ShardSimulationResponse> SimulateAsync(
        ShardSimulationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("Namespace must be provided.", nameof(request));
        }

        if (request.Nodes is null || request.Nodes.Count == 0)
        {
            throw new ArgumentException("At least one node must be provided.", nameof(request));
        }

        var resolvedStrategy = string.IsNullOrWhiteSpace(request.StrategyId)
            ? ShardHashStrategyIds.Rendezvous
            : request.StrategyId!;

        var nodeDescriptors = request.Nodes.Select(node =>
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
            {
                throw new ArgumentException("Node id cannot be empty.", nameof(request));
            }

            return new ShardNodeDescriptor
            {
                NodeId = node.NodeId,
                Weight = node.Weight.GetValueOrDefault(1),
                Region = node.Region,
                Zone = node.Zone
            };
        }).ToArray();

        var existing = await _repository.ListAsync(request.Namespace, cancellationToken).ConfigureAwait(false);
        if (existing.Count == 0)
        {
            throw new InvalidOperationException($"Namespace '{request.Namespace}' does not have any shard records.");
        }

        var definitions = existing.Select(record => new ShardDefinition
        {
            ShardId = record.ShardId,
            Capacity = record.CapacityHint
        }).ToArray();

        var hashRequest = new ShardHashRequest
        {
            Namespace = request.Namespace,
            Nodes = nodeDescriptors,
            Shards = definitions
        };

        var plan = _strategies.Compute(resolvedStrategy, hashRequest);
        var assignments = plan.Assignments.Select(ShardControlPlaneMapper.ToAssignment).ToArray();
        var lookup = existing.ToDictionary(r => r.ShardId, StringComparer.OrdinalIgnoreCase);

        var changes = plan.Assignments
            .Where(assignment => lookup.TryGetValue(assignment.ShardId, out var record) &&
                                 !string.Equals(record!.OwnerNodeId, assignment.OwnerNodeId, StringComparison.Ordinal))
            .Select(assignment => ShardControlPlaneMapper.ToChange(assignment, lookup[assignment.ShardId]))
            .ToArray();

        return new ShardSimulationResponse(
            request.Namespace,
            resolvedStrategy,
            _timeProvider.GetUtcNow(),
            assignments,
            changes);
    }
}
