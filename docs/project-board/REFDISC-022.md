# REFDISC-022 - Command Scheduling & Work Queue Abstractions

## Goal
Generalize the dispatcher’s background command scheduler/work queues into reusable abstractions so control-plane services can run recurring jobs with the same reliability, observability, and throttling controls.

## Scope
- Extract queue implementations, scheduling policies, and worker coordination logic from dispatcher (e.g., rebalancer tasks).
- Provide abstractions for queued commands, delayed jobs, and cron-like schedules.
- Integrate with backpressure/limiter primitives and lifecycle orchestrator.
- Document configuration and operational guidance.

## Requirements
1. **Reliability** - Ensure commands are executed at-least-once with retries/backoff and idempotency guidance.
2. **Scheduling flexibility** - Support immediate, delayed, and recurring schedules with configurable concurrency.
3. **Observability** - Emit metrics/logs for queue depth, processing latency, failures, and retries.
4. **Throttling** - Integrate with limiter primitives to avoid overloading downstream dependencies.
5. **Configuration** - Bind queue settings via configuration kit and allow runtime adjustments when possible.

## Deliverables
- Work queue/scheduler library (`OmniRelay.ControlPlane.Scheduling`).
- Dispatcher refactor to use the shared abstractions for existing background jobs.
- Control-plane services updated to schedule their own tasks (e.g., metadata refresh) via the library.
- Documentation covering API usage, configuration, and monitoring.

## Acceptance Criteria
- Dispatcher background jobs continue to run with identical behavior post-refactor.
- Control-plane services can schedule jobs without referencing dispatcher-specific code.
- Metrics/logs for queues remain consistent and visible in dashboards.
- Throttling/backpressure integration prevents runaway job execution.
- Library remains free of dispatcher runtime dependencies.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Validate scheduling logic (delays, cron expressions) produces expected execution times.
- Test retry/backoff policies and idempotency hooks.
- Ensure queue depth metrics update correctly.

### Integration tests
- Run a sample host with scheduled jobs, inject failures, and verify retries + observability.
- Adjust configuration at runtime to change concurrency and confirm scheduler adapts.
- Combine with limiter primitives to ensure backpressure halts/resumes job dispatch.

### Feature tests
- In OmniRelay.FeatureTests, migrate dispatcher/control-plane background jobs to the library and verify workflows (rebalancing, metadata refresh) still complete.
- Validate operator tooling (queue inspection, draining) via diagnostics endpoints.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, run high-volume job workloads to ensure queues scale and scheduling remains fair.
- Simulate cascading failures to ensure retries don’t overwhelm systems.

## Implementation status
- The TaskQueue-backed scheduler is exposed through `src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs`, giving control-plane services a drop-in `ResourceLeaseDispatcherComponent` with typed JSON contracts, safe leasing via `SafeTaskQueueWrapper`, deterministic coordination options, and integration points for peer health, replication, and throttling.
- `src/OmniRelay/ControlPlane/Throttling/BackpressureAwareRateLimiter.cs` + `ResourceLeaseBackpressureDiagnosticsListener` translate SafeTaskQueue signals into limiter toggles/diagnostics feeds, and the sample in `samples/ResourceLease.MeshDemo` wires those listeners alongside deterministic state stores so operators can observe queue depth/backpressure just like dispatcher-owned jobs.
- `tests/OmniRelay.IntegrationTests/ResourceLeaseIntegrationTests.cs` executes enqueue/lease/complete/drain cycles end-to-end (including middleware + replication fan-out) to prove the generalized scheduler matches legacy behavior, while the mesh demo host continuously runs background work to validate retries/backoff + queue draining through the shared abstractions.

## References
- Dispatcher command queue/scheduler implementations (rebalancer, metadata refresh tasks).
- REFDISC-034..037 - AOT readiness baseline and CI gating.
