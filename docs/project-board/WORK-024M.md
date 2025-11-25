# WORK-024M – http/grpc Streaming (Full-Duplex Flow Control)

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
Done

## Completion Notes
- Duplex streaming now uses bounded Hugo channels (default capacity 64, wait-on-full backpressure) in `DuplexStreamCall`, eliminating unbounded queues and aligning with streaming tutorial guidance.
- Added pipeline-friendly retry/backpressure behavior is already exercised by HTTP/GRPC duplex pumps via `Result.MapStreamAsync`/`ErrGroup`.
- New tests cover backpressure (bounded channel wait/cancel) and disposal cleanup for duplex streams.

## SLOs & CI gates
- No regressions in duplex throughput/latency; document any changes.
- CI: transport/dispatcher streaming tests; relevant integration suites.

## Testing Strategy
- Unit: simulated duplex exchange with cancellation and partial failure.
- Integration: end-to-end socket streaming scenario verifying backpressure and shutdown.
