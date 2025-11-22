# Task Queue Replication

`Hugo.TaskQueues.Replication` packages the OmniRelay replication helpers so any host can stream queue mutations, fan them out to remote peers, checkpoint progress, and capture deterministic side effects. This document explains how the lifecycle hooks, replication source, checkpointing sink, and deterministic coordinator fit together.

## Lifecycle listeners

`TaskQueue<T>` now emits `TaskQueueLifecycleEvent<T>` instances whenever work is enqueued, leased, heartbeated, completed, failed, requeued, dead-lettered, drained, or expired. Register listeners directly on the queue:

```csharp
await using var queue = new TaskQueue<ResourceLease>(new TaskQueueOptions { Name = "dispatch" });
await using var source = new TaskQueueReplicationSource<ResourceLease>(
    queue,
    new TaskQueueReplicationSourceOptions<ResourceLease>
    {
        SourcePeerId = Environment.MachineName,
        DefaultOwnerPeerId = "dispatcher-a",
        TimeProvider = FakeClock // optional
    });

await foreach (TaskQueueReplicationEvent<ResourceLease> evt in source.ReadEventsAsync(ct))
{
    _logger.LogInformation("Streamed {Kind} for seq {Sequence}", evt.Kind, evt.SequenceNumber);
}
```

When you need to observe events without draining the main channel (for example, diagnostics taps), call `source.RegisterObserver(ITaskQueueReplicationObserver<T> observer)`. `TaskQueueDiagnosticsHost` uses this hook to forward replication metadata to the `/diagnostics/taskqueue` SSE endpoint without interfering with the checkpoints powering sinks.
The source exposes both an `IAsyncEnumerable<TaskQueueReplicationEvent<T>>` and a `ChannelReader<TaskQueueReplicationEvent<T>>` so you can choose the streaming primitive that best fits your transport. Every event contains:

- Queue name, original sequence id, replication sequence number, and attempt count.
- Lifecycle kind (`Enqueued`, `LeaseGranted`, `Failed`, `LeaseExpired`, `Drained`, etc.).
- Payload + last observed error and ownership token.
- Optional owner peer id and lease expiration when applicable.
- Flags describing the origin (`FromDrain`, `FromExpiration`, `DeadLetter`).

`GoDiagnostics` now emits `taskqueue.replication.events`, `taskqueue.replication.sequence_lag`, and `taskqueue.replication.wall_clock_lag` metrics so dashboards can track replication health alongside existing queue telemetry.

## Checkpointing sinks

`CheckpointingTaskQueueReplicationSink<T>` provides the infrastructure OmniRelay previously hosted in bespoke dispatchers:

```csharp
public sealed class GrpcReplicationSink : CheckpointingTaskQueueReplicationSink<ResourceLease>
{
    private readonly ReplicationClient _client;

    public GrpcReplicationSink(
        ReplicationClient client,
        ITaskQueueReplicationCheckpointStore checkpointStore,
        TimeProvider? timeProvider = null)
        : base("omnirelay.dispatch", checkpointStore, timeProvider)
    {
        _client = client;
    }

    protected override async ValueTask ApplyEventAsync(TaskQueueReplicationEvent<ResourceLease> evt, CancellationToken token)
    {
        await _client.ForwardAsync(evt, token);
    }
}
```

Implement `ITaskQueueReplicationCheckpointStore` using your preferred persistence technology (SQL row, Cosmos document, deterministic state, etc.). The base class maintains the global checkpoint plus per-peer offsets so a single `ITaskQueueReplicationCheckpointStore` supports HTTP, gRPC, SignalR, and queue-based sinks simultaneously:

```csharp
public sealed class BlobCheckpointStore : ITaskQueueReplicationCheckpointStore
{
    private readonly BlobClient _blob;

    public async ValueTask<TaskQueueReplicationCheckpoint> ReadAsync(string streamId, CancellationToken token) =>
        await _blob.ExistsAsync(token)
            ? await _blob.DownloadContentAsync(token)
                is { Value.Content: { } payload }
                ? JsonSerializer.Deserialize<TaskQueueReplicationCheckpoint>(payload)
                : TaskQueueReplicationCheckpoint.Empty(streamId)
            : TaskQueueReplicationCheckpoint.Empty(streamId);

    public async ValueTask PersistAsync(TaskQueueReplicationCheckpoint checkpoint, CancellationToken token) =>
        await _blob.UploadAsync(BinaryData.FromObjectAsJson(checkpoint), overwrite: true, cancellationToken: token);
}
```

`ProcessAsync` can consume the live stream (`source.ReadEventsAsync(ct)`) or an intermediate channel when you need backpressure between deserialisation and network forwarding.

## Deterministic replay

Replication events often drive HTTP/gRPC calls or database writes that must be idempotent when a sink restarts. `TaskQueueDeterministicCoordinator<T>` wires replication event ids into `DeterministicEffectStore` so the work only runs once:

```csharp
var deterministicOptions = TaskQueueReplicationJsonSerialization.CreateOptions<ResourceLease>();
var effectStore = new DeterministicEffectStore(stateStore, timeProvider, serializerOptions: deterministicOptions);
var coordinator = new TaskQueueDeterministicCoordinator<ResourceLease>(effectStore);

protected override Task ApplyEventAsync(TaskQueueReplicationEvent<ResourceLease> evt, CancellationToken token) =>
    coordinator.ExecuteAsync(evt, async (e, ct) =>
    {
        await _http.ReplicateAsync(e, ct);
        return Result.Ok(Unit.Value);
    }, token);
```

Even if the sink reprocesses events because a checkpoint was lost or intentionally replayed, the deterministic store returns the original result without running `_http.ReplicateAsync` again.

## Migration checklist

1. Reference `Hugo.TaskQueues.Replication` and configure your queue(s) to use the default lifecycle hooks.
2. Replace bespoke replication writers with a `TaskQueueReplicationSource<T>` and one or more sinks derived from `CheckpointingTaskQueueReplicationSink<T>`.
3. Implement `ITaskQueueReplicationCheckpointStore` backed by your preferred persistence (SQL, Cosmos, blob, or `IDeterministicStateStore`).
4. Wrap side effects in `TaskQueueDeterministicCoordinator<T>` so replayed events do not re-run HTTP calls or database writes.
5. Monitor the new `taskqueue.replication.*` metrics to alert on lag, and export `TaskQueueReplicationEvent` payloads by calling `TaskQueueReplicationJsonSerialization.CreateOptions<T>()` when building `DeterministicEffectStore` instances or custom serializers.
