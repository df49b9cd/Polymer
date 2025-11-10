using Hugo;

namespace OmniRelay.Dispatcher;

/// <summary>Coordinates deterministic capture of table lease replication events.</summary>
public interface ITableLeaseDeterministicCoordinator
{
    ValueTask RecordAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken);
}

/// <summary>Options needed to wire a <see cref="DeterministicTableLeaseCoordinator"/>.</summary>
public sealed class TableLeaseDeterministicOptions
{
    /// <summary>The deterministic state store backing VersionGate and DeterministicEffectStore.</summary>
    public IDeterministicStateStore StateStore { get; init; } = new InMemoryDeterministicStateStore();

    /// <summary>Logical change identifier recorded in VersionGate.</summary>
    public string ChangeId { get; init; } = "tablelease.replication";

    /// <summary>Minimum supported change version.</summary>
    public int MinVersion { get; init; } = 1;

    /// <summary>Maximum supported change version.</summary>
    public int MaxVersion { get; init; } = 1;

    /// <summary>Optional factory that customizes the deterministic effect identifier per event.</summary>
    public Func<TableLeaseReplicationEvent, string>? EffectIdFactory { get; init; }
}

/// <summary>
/// Default deterministic coordinator that captures each replication event via <see cref="DeterministicEffectStore"/> and
/// <see cref="DeterministicGate"/> so failovers can replay the exact sequence without duplication.
/// </summary>
public sealed class DeterministicTableLeaseCoordinator : ITableLeaseDeterministicCoordinator
{
    private readonly DeterministicEffectStore _effectStore;
    private readonly DeterministicGate _gate;
    private readonly string _changeId;
    private readonly int _minVersion;
    private readonly int _maxVersion;
    private readonly Func<TableLeaseReplicationEvent, string> _effectIdFactory;

    public DeterministicTableLeaseCoordinator(TableLeaseDeterministicOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stateStore = options.StateStore ?? throw new ArgumentException("StateStore is required for deterministic coordination.", nameof(options));

        _effectStore = new DeterministicEffectStore(stateStore);
        _gate = new DeterministicGate(new VersionGate(stateStore), _effectStore);
        _changeId = string.IsNullOrWhiteSpace(options.ChangeId) ? "tablelease.replication" : options.ChangeId!;
        _minVersion = Math.Max(1, options.MinVersion);
        _maxVersion = Math.Max(_minVersion, options.MaxVersion);
        _effectIdFactory = options.EffectIdFactory ?? (evt => $"{_changeId}/seq/{evt.SequenceNumber}");
    }

    public async ValueTask RecordAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        var changeScope = $"{_changeId}.{replicationEvent.SequenceNumber}";
        var effectId = _effectIdFactory(replicationEvent);

        var result = await _gate.ExecuteAsync(
            changeScope,
            _minVersion,
            _maxVersion,
            (_, ct) => CaptureEffectAsync(effectId, replicationEvent, ct),
            _ => _maxVersion,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }
    }

    private async Task<Result<long>> CaptureEffectAsync(string effectId, TableLeaseReplicationEvent evt, CancellationToken cancellationToken) =>
        await _effectStore.CaptureAsync<long>(
            effectId,
            _ => Task.FromResult(Result.Ok(evt.SequenceNumber)),
            cancellationToken).ConfigureAwait(false);
}
