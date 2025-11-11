using System.Collections.Concurrent;
using OmniRelay.Dispatcher;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal sealed class MeshReplicationLog
{
    private readonly ConcurrentQueue<ResourceLeaseReplicationEvent> _events = new();
    private readonly int _capacity;

    public MeshReplicationLog(int capacity = 32)
    {
        _capacity = Math.Max(8, capacity);
    }

    public void Add(ResourceLeaseReplicationEvent evt)
    {
        _events.Enqueue(evt);
        while (_events.Count > _capacity && _events.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<ResourceLeaseReplicationEvent> GetRecent() => _events.ToArray();
}

internal sealed class MeshReplicationLogSink : IResourceLeaseReplicationSink
{
    private readonly MeshReplicationLog _log;

    public MeshReplicationLogSink(MeshReplicationLog log) => _log = log;

    public ValueTask ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        _log.Add(replicationEvent);
        return ValueTask.CompletedTask;
    }
}
