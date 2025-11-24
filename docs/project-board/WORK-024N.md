# WORK-024N – Socket Oneway (Fire-and-Forget) with Compensations

## Goal
Align socket oneway (fire-and-forget) flows with Hugo compensations and backpressure as described in the `socket-oneway` tutorial.

## Scope
- Oneway socket send/receive paths under `src/OmniRelay.DataPlane/Transport/*Socket*` and dispatcher adapters that treat messages as fire-and-forget.

## Acceptance Criteria
- Oneway sends use bounded channels and `Result` pipelines; failures surface as `Result` (no thrown exceptions).
- Compensations recorded for buffers/resources; backpressure applied (drop/queue strategy documented).
- Tests assert no hangs on shutdown and that failed sends execute compensations.

## Status
Planned

## SLOs & CI gates
- Maintain oneway throughput without unbounded buffering.
- CI: transport/dispatcher oneway unit/integration tests.

## Testing Strategy
- Unit: drop/backpressure behavior; compensation on failure.
- Integration: fire-and-forget scenario ensuring shutdown drains/cleans safely.
