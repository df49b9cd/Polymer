using System.Collections.Concurrent;

namespace OmniRelay.Diagnostics;

internal sealed class ProbeSnapshotStore : IProbeSnapshotProvider
{
    private readonly ConcurrentDictionary<string, ProbeExecutionSnapshot> _snapshots = new(StringComparer.Ordinal);

    public void Record(ProbeExecutionSnapshot snapshot)
    {
        _snapshots[snapshot.Name] = snapshot;
    }

    public IReadOnlyCollection<ProbeExecutionSnapshot> Snapshot() => _snapshots.Values.ToList();
}

/// <summary>Provides probe execution snapshots for diagnostics endpoints.</summary>
public interface IProbeSnapshotProvider
{
    IReadOnlyCollection<ProbeExecutionSnapshot> Snapshot();
}
