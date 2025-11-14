# REFDISC-020 - Backpressure & Rate-Limiting Primitives

## Goal
Extract backpressure-aware rate limiters and middleware into a shared library so dispatcher and control-plane services enforce consistent throttling policies and telemetry without duplicating implementations.

## Scope
- Isolate `BackpressureAwareRateLimiter`, limiter factories, and HTTP middleware from dispatcher/samples.
- Provide generic primitives for token-bucket, sliding-window, and adaptive backpressure strategies.
- Integrate with diagnostics runtime to expose limiter status and allow operators to adjust thresholds.
- Document how services configure, monitor, and tune limiters.

## Requirements
1. **Limiter variety** - Support multiple limiter strategies (fixed token bucket, adaptive) with pluggable algorithms.
2. **Backpressure signaling** - Provide hooks to switch between normal and degraded modes, propagating signals to callers.
3. **Telemetry** - Emit metrics/logs for permit usage, drops, and backpressure state changes.
4. **Middleware integration** - Offer HTTP/gRPC middleware that applies limiters consistently across hosts.
5. **Configuration** - Bind limiter settings via configuration kit with validation and runtime adjustments.

## Deliverables
- Limiter/Backpressure library under `OmniRelay.ControlPlane.Throttling`.
- Dispatcher refactor to use the shared primitives.
- Control-plane services wired to apply limiters where needed (HTTP endpoints, gossip operations).
- Documentation covering strategy selection, configuration, and observability.

## Acceptance Criteria
- Existing rate-limiting behavior in dispatcher/samples remains unchanged after migration.
- Control-plane services can apply limiters and observe backpressure metrics without referencing dispatcher code.
- Operator tools can adjust limiter thresholds via configuration or diagnostics endpoints.
- Telemetry dashboards display limiter metrics for all hosts using the library.
- Library is free of dispatcher-specific dependencies.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Validate limiter algorithms (token refill, adaptive switching) under deterministic time providers.
- Test backpressure signal propagation and state transitions.
- Ensure configuration validation catches invalid thresholds.

### Integration tests
- Apply limiters to HTTP/gRPC hosts, simulate high load, and verify throttling + telemetry behavior.
- Toggle limiter settings at runtime via diagnostics endpoints to confirm dynamic updates.
- Combine normal + degraded limiters to ensure transitions work without request loss.

### Feature tests
- In OmniRelay.FeatureTests, enable limiters on dispatcher/control-plane endpoints and run load scenarios, ensuring behavior matches expectations.
- Validate operator workflows (enable backpressure mode) through diagnostics UI/CLI.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, run large-scale traffic through the limiters to ensure they scale and maintain fairness.
- Inject rapid load spikes to confirm adaptive backpressure engages without oscillation.

## Implementation status
- `src/OmniRelay/ControlPlane/Throttling/BackpressureAwareRateLimiter.cs` now owns the shared limiter selector plus the `RateLimitingBackpressureListener` and `ResourceLeaseBackpressureDiagnosticsListener`, giving every host a single component that can flip between normal and degraded rate limiters while streaming state changes through a diagnostics-friendly channel.
- `ResourceLeaseDispatcher` exposes the listener hook via `ResourceLeaseDispatcherOptions.BackpressureListener` (`src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs`), so dispatcher instances and control-plane agents can toggle the limiter gates whenever the SafeTaskQueue reports pressure. The ResourceLease mesh sample wires the listener + limiter selector together (`samples/ResourceLease.MeshDemo/Program.cs`) and exposes `/demo/backpressure` for operator feedback, proving the configuration + diagnostics flow end to end.
- `tests/OmniRelay.Dispatcher.UnitTests/ResourceLeaseBackpressureListenerTests.cs` cover limiter switching, diagnostics buffering, and logging, providing regression coverage for the shared primitives the story introduced.

## References
- `BackpressureAwareRateLimiter` and related middleware in samples/transport.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
