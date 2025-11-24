# WORK-024M – Socket Streaming (Full-Duplex Flow Control)

## Goal
Implement full-duplex socket streaming using Hugo channels, wait groups, and result streams per the `socket-streaming` tutorial.

## Scope
- Duplex socket handlers/clients (when present) under `src/OmniRelay.DataPlane/Transport/*Socket*` plus dispatcher streaming adapters.
- Flow control/backpressure integration points (channels, bounded buffers) in transport/dispatcher streams.

## Acceptance Criteria
- Inbound/outbound streams use Hugo channels with bounded capacity; processing uses `Result.MapStreamAsync`/tap helpers with cancellation linked.
- Backpressure and shutdown rely on `WaitGroup`/channel completion; no unbounded queues.
- Tests cover bidirectional streaming with cancellation mid-flight and resource cleanup.

## Status
Planned

## SLOs & CI gates
- No regressions in duplex throughput/latency; document any changes.
- CI: transport/dispatcher streaming tests; relevant integration suites.

## Testing Strategy
- Unit: simulated duplex exchange with cancellation and partial failure.
- Integration: end-to-end socket streaming scenario verifying backpressure and shutdown.
