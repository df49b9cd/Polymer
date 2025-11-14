# REFDISC-032 - Control-Plane Event Bus

## Goal
Extract the dispatcher’s internal event dispatch (peer status updates, transport events, leadership changes) into a shared event bus so all services can publish/subscribe to lifecycle events consistently.

## Scope
- Define event contracts for key lifecycle events (gossip membership, leadership, transport health).
- Provide an in-process event bus with subscription filters and async delivery.
- Integrate with telemetry/logging to surface event flow metrics.
- Document usage patterns and extension points.

## Requirements
1. **Typed events** - Use strongly typed events with versioning and metadata.
2. **Subscription filtering** - Allow listeners to filter by cluster, role, or event type.
3. **Backpressure** - Prevent slow subscribers from blocking publishers (buffering, drop policies).
4. **Observability** - Emit metrics/logs for event publish counts, subscriber lag, and errors.
5. **Integration** - Event bus must be dispatcher-agnostic and usable by gossip, leadership, and diagnostics services.

## Deliverables
- Event bus library (`OmniRelay.ControlPlane.Events`).
- Dispatcher refactor to publish events via the bus and update subscribers.
- Control-plane services updated to consume/publish events through the bus.
- Documentation covering event schemas, subscription APIs, and observability.

## Acceptance Criteria
- Existing event-driven functionality (e.g., diagnostics responding to peer changes) continues working after migration.
- Control-plane services can subscribe to events without referencing dispatcher internals.
- Bus handles high event volumes without dropping critical notifications unless configured.
- Telemetry dashboards display event throughput/lag metrics.
- Library has no dispatcher-specific dependencies.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Validate subscription filtering and ensure subscribers receive only relevant events.
- Test buffering/backpressure behavior when subscribers are slow.
- Ensure event metadata/versioning works as intended.

### Integration tests
- Publish events from gossip/leadership components and verify subscribers (diagnostics, telemetry) react appropriately.
- Simulate subscriber failures and confirm bus handles them gracefully.
- Measure event latency and ensure it stays within targets.

### Feature tests
- In OmniRelay.FeatureTests, use the event bus to wire diagnostics/CLI notifications and ensure user-facing behavior matches pre-refactor state.
- Validate operator tools that rely on event-driven updates (e.g., watch commands) continue functioning.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, push high volumes of events to ensure bus scales, maintains ordering guarantees where necessary, and surfaces lag metrics.
- Stress test subscriber churn (many subscribers joining/leaving) to confirm stability.

## Implementation status
- `ControlPlaneEventBus` now lives in `OmniRelay.ControlPlane.Events` and is wired through the gossip + leadership stacks. `MeshGossipHost` publishes membership changes on every envelope, sweep, or forced-status transition, and the dedicated diagnostics host subscribes to the bus when serving `/control/peers` so CLI users see immediate updates even before the registry is online. Leadership streaming reuses the same bus for SSE and the new gRPC control-plane host, keeping diagnostics and tooling in sync.

## References
- Current event handling spread across gossip/leadership diagnostics code.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
