using System.Collections.Immutable;
using Hugo;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Snapshot of orchestrated lifecycle components.</summary>
public sealed record LifecycleOrchestratorSnapshot(
    DateTimeOffset CapturedAt,
    ImmutableArray<LifecycleComponentSnapshot> Components);

/// <summary>Snapshot representing a single component status.</summary>
public sealed record LifecycleComponentSnapshot(
    string Name,
    LifecycleComponentStatus Status,
    Error? LastError);
