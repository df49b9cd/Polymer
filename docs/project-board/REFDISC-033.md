# REFDISC-033 - Upgrade & Drain Orchestration Kit

## Goal
Generalize the dispatcher’s rolling upgrade, drain/cordon, and status reporting logic into a reusable kit so any host can participate in coordinated deployments with consistent operator experiences.

## Scope
- Extract drain/cordon APIs, status tracking, and rollout coordination hooks from dispatcher control-plane endpoints.
- Provide services to track node state (active, draining, drained), report readiness, and block traffic as needed.
- Integrate with diagnostics runtime and event bus for status updates.
- Document operator workflows and API contracts.

## Requirements
1. **State machine** - Define canonical states (active, draining, drained, failed) with transitions and validation.
2. **Traffic control** - Provide hooks to stop accepting new work (HTTP/gRPC) and gracefully finish in-flight tasks.
3. **Coordination** - Allow external orchestrators/CLI to query and drive state transitions via APIs.
4. **Telemetry** - Emit metrics/logs for drains, failures, and rollout progress.
5. **Compatibility** - Kit must work for dispatcher and auxiliary services without bespoke logic.

## Deliverables
- Upgrade/drain orchestration library (`OmniRelay.ControlPlane.Upgrade`).
- Dispatcher refactor to use the kit for its existing drain/cordon workflows.
- Control-plane services updated to expose the same APIs/state transitions.
- Documentation covering API usage, CLI integration, and rollout best practices.

## Acceptance Criteria
- Dispatcher drain/cordon workflows behave identically after migration.
- Control-plane services can be drained/cordoned via the same APIs and participate in orchestrated upgrades.
- CLI/operator tooling displays consistent status/info for all nodes.
- Metrics/logs reflect rollout progress and issues uniformly.
- Kit contains no dispatcher-only dependencies.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Validate state machine transitions and guardrails (no invalid transitions).
- Test hooks for traffic blocking and ensure they are invoked during drain.
- Verify status reporting objects include required metadata.

### Integration tests
- Drive drain/cordon flows against sample hosts, ensuring HTTP/gRPC traffic is quiesced and diagnostics endpoints reflect new states.
- Simulate failures (drain timeout) and confirm state machine enters failed state with logs/metrics.
- Test coordination via CLI/API to ensure controllers can orchestrate multi-node upgrades.

### Feature tests
- In OmniRelay.FeatureTests, run rolling upgrade scenarios using the kit for dispatcher/control-plane services, confirming convergence and operator visibility.
- Validate CLI commands for drain/cordon interact with all hosts uniformly.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, coordinate drains across large clusters, ensuring kit scales and prevents simultaneous pool depletion.
- Stress concurrent drains to ensure state tracking remains consistent.

## Implementation status
- `NodeDrainCoordinator`, participant interfaces, and snapshot types ship under `src/OmniRelay/ControlPlane/Upgrade/`, giving every host a shared state machine that orchestrates drain/cordon workflows across HTTP and gRPC inbounds. `HttpInbound` and `GrpcInbound` now implement `INodeDrainParticipant` so the coordinator can flip their drain gates without ripping through `StopAsync`, and dispatcher wiring registers them by name so diagnostics can show their status.
- Diagnostics and CLI tooling expose the workflow end to end: `/control/upgrade`, `/control/upgrade/drain`, and `/control/upgrade/resume` surface on `DiagnosticsControlPlaneHost`, returning rich snapshots that include per-participant state and errors. `omnirelay mesh upgrade status|drain|resume` drives those endpoints, prints human-readable summaries or JSON, and records optional reasons for drains—operators no longer have to flip bespoke flags during rollouts.

## References
- Existing drain/cordon logic in dispatcher diagnostics/control endpoints.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
