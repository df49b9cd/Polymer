# REFDISC-011 - Shared Lifecycle Orchestrator

## Goal
Generalize the dispatcher’s lifecycle component orchestration (start/stop sequencing, retry policies) into a reusable orchestrator so control-plane services can manage background components with the same reliability guarantees.

## Scope
- Extract lifecycle descriptor processing, dependency ordering, and start/stop retry logic from `Dispatcher`.
- Provide an `ILifecycleOrchestrator` abstraction that accepts components (`ILifecycle` or delegates) and manages them with configurable policies.
- Integrate logging hooks for start/stop successes/failures identical to dispatcher behavior.
- Document how the orchestrator is wired into dispatcher and control-plane service hosts.

## Requirements
1. **Deterministic ordering** - Honor declared component dependencies and ensure consistent start/stop orderings.
2. **Retry policies** - Support configurable retry/backoff for both start and stop operations (mirroring `ResultExecutionPolicy` usage).
3. **Cancellation + disposal** - Properly propagate cancellation tokens and dispose components even when failures occur.
4. **Diagnostics** - Emit structured logs/metrics for lifecycle transitions and expose status for health endpoints.
5. **Minimal dependency** - Orchestrator must not depend on dispatcher-specific types beyond `ILifecycle`.

## Deliverables
- Lifecycle orchestrator implementation + interfaces under `OmniRelay.Core.Hosting` (or similar).
- Dispatcher refactored to use the orchestrator instead of bespoke logic.
- Control-plane services updated to register components (gossip host, diagnostics host, HTTP listeners) via the orchestrator.
- Documentation detailing usage patterns, dependency declarations, and configuration.

## Acceptance Criteria
- Dispatcher lifecycle behavior (ordering, retries, logging) remains unchanged post-migration.
- Control-plane services can add background components (e.g., gossip loops) to the orchestrator and benefit from retries + logging.
- Health/diagnostic surfaces can query orchestrator status to present component states.
- Orchestrator handles cancellation gracefully, ensuring `StopAsync` completes without leaking tasks.
- No dispatcher-only references remain inside the orchestrator package.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Validate dependency ordering with graphs including cycles (should error) and branching paths.
- Test retry policies by injecting failing components and asserting retries/backoff occur as configured.
- Ensure cancellation tokens propagate and that `StopAsync` handles exceptions without preventing other components from stopping.

### Integration tests
- Wire orchestrator into a sample host with multiple components (HTTP server, gossip loop) and verify coordinated start/stop sequences.
- Simulate component failures mid-run to ensure orchestrator logs appropriately and attempts recovery/retries.
- Confirm orchestrator status reporting integrates with diagnostics endpoints.

### Feature tests
- In OmniRelay.FeatureTests, run dispatcher and control-plane hosts with orchestrator-managed components, orchestrate rolling restarts, and ensure lifecycle logs/metrics match expectations.
- Validate operator workflows (drain/cordon) that depend on predictable start/stop behavior continue to work.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, manage many components per host (gossip, leadership, telemetry exporters) to ensure orchestrator scales without deadlocks or long stop times.
- Inject chaos (random component failure) and ensure orchestrator retries/backoff without cascading failures.

## Implementation status
- `ILifecycleOrchestrator`, `LifecycleComponentRegistration`, and the concrete `LifecycleOrchestrator` now live under `src/OmniRelay/ControlPlane/Hosting/` and encapsulate dependency ordering, retries, and diagnostics snapshots for any collection of `ILifecycle` components. The dispatcher constructor builds registrations from `DispatcherOptions`, feeds them into the orchestrator, and delegates start/stop so transports, gossip, leadership, and control-plane hosts all run through the shared coordinator instead of bespoke loops.
- `DispatcherBuilder` records explicit dependencies for gossip (`mesh-gossip`), leadership (`mesh-leadership`), and the control-plane hosts (`control-plane:http`, `control-plane:grpc`), so the orchestrator enforces the ordering guarantees the story requires. Because the orchestrator is part of the public `ControlPlane.Hosting` surface, other hosts can reuse it (e.g., tests or future control-plane services) without referencing dispatcher internals.

## References
- `src/OmniRelay/Dispatcher/Dispatcher.cs` - Current lifecycle management logic.
- `docs/architecture/service-discovery.md` - Component lifecycle requirements.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
