using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Hugo;
using Hugo.Policies;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Shards.Hashing;
using static Hugo.Go;

namespace OmniRelay.Core.Shards.ControlPlane;

public sealed partial class ShardControlPlaneService
{
    private static readonly ResultExecutionPolicy DefaultRepositoryPolicy =
        ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.Exponential(
            3,
            TimeSpan.FromMilliseconds(25),
            2.0,
            TimeSpan.FromMilliseconds(500)));

    private readonly IShardRepository _repository;
    private readonly ShardHashStrategyRegistry _strategies;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ShardControlPlaneService> _logger;
    private readonly ResultExecutionPolicy _repositoryPolicy;

    public ShardControlPlaneService(
        IShardRepository repository,
        ShardHashStrategyRegistry strategies,
        TimeProvider? timeProvider,
        ILogger<ShardControlPlaneService> logger,
        ResultExecutionPolicy? repositoryPolicy = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _repositoryPolicy = repositoryPolicy ?? DefaultRepositoryPolicy;
    }

    public async ValueTask<Result<ShardListResponse>> ListAsync(
        ShardFilter filter,
        string? cursorToken,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (filter is null)
        {
            return Err<ShardListResponse>(ShardControlPlaneErrors.FilterRequired());
        }

        ShardQueryCursor? cursor = null;

        if (!string.IsNullOrWhiteSpace(cursorToken) &&
            !ShardQueryCursor.TryParse(cursorToken, out cursor))
        {
            return Err<ShardListResponse>(ShardControlPlaneErrors.InvalidCursor(cursorToken));
        }

        var options = filter.ToQueryOptions(pageSize, cursor);
        return await Result.RetryWithPolicyAsync<ShardListResponse>(
            async (_, token) =>
            {
                var result = await _repository.QueryAsync(options, token).ConfigureAwait(false);
                var items = result.Items.Select(ShardControlPlaneMapper.ToSummary).ToArray();
                return Ok(new ShardListResponse(items, result.NextCursor?.Encode(), result.HighestVersion));
            },
            _repositoryPolicy,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<ShardDiffResponse>> DiffAsync(
        long? fromPosition,
        long? toPosition,
        ShardFilter filter,
        CancellationToken cancellationToken)
    {
        if (filter is null)
        {
            return Err<ShardDiffResponse>(ShardControlPlaneErrors.FilterRequired());
        }

        var since = fromPosition.HasValue ? Math.Max(0, fromPosition.Value - 1) : (long?)null;
        var upperBound = toPosition ?? long.MaxValue;
        var diffs = new List<ShardDiffEntry>();

        try
        {
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
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
        {
            return Err<ShardDiffResponse>(Error.Canceled("Shard diff canceled.", cancellationToken)
                .WithMetadata("stage", "shards.diff"));
        }
        catch (Exception ex)
        {
            Log.DiffStreamFailed(_logger, ex);
            return Err<ShardDiffResponse>(ShardControlPlaneErrors.StreamFailure(ex, "shards.diff"));
        }

        var last = diffs.Count > 0 ? diffs[^1].Position : (long?)null;
        return Ok(new ShardDiffResponse(diffs, last));
    }

    public IAsyncEnumerable<Result<ShardRecordDiff>> WatchAsync(
        long? resumeToken,
        ShardFilter filter,
        CancellationToken cancellationToken)
    {
        if (filter is null)
        {
            return AsyncEnumerable.Repeat(
                Err<ShardRecordDiff>(ShardControlPlaneErrors.FilterRequired()),
                1);
        }

        return Result.FilterStreamAsync(
            StreamDiffs(resumeToken, cancellationToken),
            diff => filter.Matches(diff.Current),
            cancellationToken);
    }

    public ValueTask<Result<IReadOnlyList<ShardRecordDiff>>> CollectWatchAsync(
        long? resumeToken,
        ShardFilter filter,
        CancellationToken cancellationToken) =>
        Result.CollectErrorsAsync(WatchAsync(resumeToken, filter, cancellationToken), cancellationToken);

    public async ValueTask<Result<ShardSimulationResponse>> SimulateAsync(
        ShardSimulationRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Err<ShardSimulationResponse>(ShardControlPlaneErrors.SimulationRequestRequired());
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            return Err<ShardSimulationResponse>(ShardControlPlaneErrors.NamespaceRequired());
        }

        if (request.Nodes is null || request.Nodes.Count == 0)
        {
            return Err<ShardSimulationResponse>(ShardControlPlaneErrors.NodesRequired());
        }

        var resolvedStrategy = string.IsNullOrWhiteSpace(request.StrategyId)
            ? ShardHashStrategyIds.Rendezvous
            : request.StrategyId!;

        var nodeDescriptors = new List<ShardNodeDescriptor>(request.Nodes.Count);
        foreach (var node in request.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.NodeId))
            {
                return Err<ShardSimulationResponse>(ShardControlPlaneErrors.NodeIdInvalid(node?.NodeId));
            }

            nodeDescriptors.Add(new ShardNodeDescriptor
            {
                NodeId = node.NodeId,
                Weight = node.Weight.GetValueOrDefault(1),
                Region = node.Region,
                Zone = node.Zone
            });
        }

        var existingResult = await Result.RetryWithPolicyAsync<IReadOnlyList<ShardRecord>>(
            async (_, token) =>
            {
                var records = await _repository.ListAsync(request.Namespace, token).ConfigureAwait(false);
                return Ok((IReadOnlyList<ShardRecord>)records);
            },
            _repositoryPolicy,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (existingResult.IsFailure)
        {
            return Err<ShardSimulationResponse>(existingResult.Error);
        }

        var existing = existingResult.Value;
        if (existing.Count == 0)
        {
            Log.SimulationNamespaceMissing(_logger, request.Namespace);
            return Err<ShardSimulationResponse>(ShardControlPlaneErrors.NamespaceMissing(request.Namespace));
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
        if (plan.IsFailure)
        {
            return Err<ShardSimulationResponse>(plan.Error);
        }

        var assignments = plan.Value.Assignments.Select(ShardControlPlaneMapper.ToAssignment).ToArray();
        if (assignments.Length == 0)
        {
            return Ok(new ShardSimulationResponse(
                request.Namespace,
                resolvedStrategy,
                _timeProvider.GetUtcNow(),
                assignments,
                Array.Empty<ShardSimulationChange>()));
        }

        var lookup = existing.ToDictionary(r => r.ShardId, StringComparer.OrdinalIgnoreCase);
        var assignmentLookup = plan.Value.Assignments.ToDictionary(a => a.ShardId, StringComparer.OrdinalIgnoreCase);

        var missingAssignment = assignments.FirstOrDefault(a => !lookup.ContainsKey(a.ShardId));
        if (missingAssignment is not null)
        {
            return Err<ShardSimulationResponse>(ShardControlPlaneErrors.AssignmentMissing(missingAssignment.ShardId));
        }

        var workerCount = Math.Min(assignments.Length, Environment.ProcessorCount);
        var partitionSize = (int)Math.Ceiling(assignments.Length / (double)workerCount);
        var partitions = assignments.Chunk(partitionSize).ToArray();
        workerCount = partitions.Length;

        var changeReaders = new List<ChannelReader<ShardSimulationChange>>(workerCount);
        var operations = new List<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<Unit>>>>(workerCount);

        foreach (var slice in partitions)
        {
            var changeChannel = MakeChannel<ShardSimulationChange>(new BoundedChannelOptions(Math.Max(16, slice.Length))
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            changeReaders.Add(changeChannel.Reader);

            operations.Add(async (ctx, ct) =>
            {
                try
                {
                    foreach (var assignment in slice)
                    {
                        if (!lookup.TryGetValue(assignment.ShardId, out var record))
                        {
                            changeChannel.Writer.TryComplete();
                            return Err<Unit>(ShardControlPlaneErrors.AssignmentMissing(assignment.ShardId));
                        }

                        if (string.Equals(assignment.LocalityHint, "fail", StringComparison.OrdinalIgnoreCase))
                        {
                            changeChannel.Writer.TryComplete();
                            return Err<Unit>(ShardControlPlaneErrors.AssignmentFailed(assignment.ShardId, "Simulated worker failure."));
                        }

                        if (!string.Equals(record.OwnerNodeId, assignment.OwnerNodeId, StringComparison.Ordinal))
                        {
                            var sourceAssignment = assignmentLookup[assignment.ShardId];
                            var change = ShardControlPlaneMapper.ToChange(sourceAssignment, record);
                            await changeChannel.Writer.WriteAsync(change, ct).ConfigureAwait(false);
                        }
                    }

                    changeChannel.Writer.TryComplete();
                    return Ok(Unit.Value);
                }
                catch (OperationCanceledException oce) when (oce.CancellationToken == ct)
                {
                    changeChannel.Writer.TryComplete(oce);
                    return Err<Unit>(Error.Canceled("Shard simulation canceled.", oce.CancellationToken));
                }
                catch (Exception ex)
                {
                    changeChannel.Writer.TryComplete(ex);
                    return Err<Unit>(Error.FromException(ex));
                }
            });
        }

        var fanOut = await ResultPipeline.FanOutAsync(
            operations,
            _repositoryPolicy,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (fanOut.IsFailure)
        {
            return Err<ShardSimulationResponse>(fanOut.Error);
        }

        var mergedChannel = MakeChannel<ShardSimulationChange>(new BoundedChannelOptions(assignments.Length)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var mergeResult = await Result.RetryWithPolicyAsync<Go.Unit>(
            (ctx, ct) => ResultPipelineChannels.MergeAsync(
                ctx,
                changeReaders,
                mergedChannel.Writer,
                completeDestination: true,
                timeout: null,
                ct),
            _repositoryPolicy,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (mergeResult.IsFailure)
        {
            return Err<ShardSimulationResponse>(mergeResult.Error);
        }

        var changes = new List<ShardSimulationChange>(assignments.Length);
        await foreach (var change in mergedChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(change);
        }

        return Ok(new ShardSimulationResponse(
            request.Namespace,
            resolvedStrategy,
            _timeProvider.GetUtcNow(),
            assignments,
            changes));
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Shard diff stream failed.")]
        public static partial void DiffStreamFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Shard simulation requested for namespace {Namespace} but no shard records exist.")]
        public static partial void SimulationNamespaceMissing(ILogger logger, string @namespace);
    }

    private IAsyncEnumerable<Result<ShardRecordDiff>> StreamDiffs(
        long? resumeToken,
        CancellationToken cancellationToken)
    {
        return Stream(cancellationToken);

        async IAsyncEnumerable<Result<ShardRecordDiff>> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            var enumerator = _repository.StreamDiffsAsync(resumeToken, ct).GetAsyncEnumerator(ct);
            Result<ShardRecordDiff>? failure = null;
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException oce) when (oce.CancellationToken == ct)
                    {
                        failure = Err<ShardRecordDiff>(Error.Canceled("Shard watch canceled.", ct));
                        break;
                    }
                    catch (Exception ex)
                    {
                        failure = Err<ShardRecordDiff>(ShardControlPlaneErrors.StreamFailure(ex, "shards.watch"));
                        break;
                    }

                    if (!hasNext)
                    {
                        yield break;
                    }

                    yield return Ok(enumerator.Current);
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (failure is not null)
            {
                yield return failure.Value;
            }
        }
    }
}
