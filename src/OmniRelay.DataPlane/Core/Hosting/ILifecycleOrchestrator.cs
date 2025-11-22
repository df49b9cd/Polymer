using Hugo;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Coordinates start/stop sequencing for registered lifecycle components.</summary>
public interface ILifecycleOrchestrator
{
    /// <summary>Starts all registered components in dependency order.</summary>
    ValueTask<Result<Unit>> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops all registered components in reverse dependency order.</summary>
    ValueTask<Result<Unit>> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Produces a snapshot of the orchestrator state for diagnostics.</summary>
    LifecycleOrchestratorSnapshot Snapshot();
}
