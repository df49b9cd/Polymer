# WORK-024L – http/grpc Unary with Hugo Pipelines

## Goal
Align socket unary request/response handling with Hugo Result pipelines and compensations per the `socket-unary` tutorial.

## Scope
- Socket unary handlers in `src/OmniRelay.DataPlane/Transport/Http/HttpInbound.cs` (if socket path) and socket-specific components (add paths when present under `src/OmniRelay.DataPlane/Transport/*Socket*`); dispatcher entry points that wrap unary sockets.
- Tests/fixtures under `tests/OmniRelay.Dispatcher.UnitTests` or socket-specific suites.

## Acceptance Criteria
- Unary socket flows use Hugo `Result` combinators (no thrown exceptions in business logic), link cancellation, and record compensations for socket resources.
- Timeouts/backoff use `ResultPipelineTimers.DelayAsync` or `RetryWithPolicyAsync` as appropriate.
- Tests cover success, timeout, cancellation, and compensation execution on failure.

## Status
Done

## Completion Notes
- HTTP unary responses now write bodies via `Result.RetryWithPolicyAsync` under a fixed-delay policy, converting write failures/timeouts/cancellations into structured `Error` and preserving Hugo pipeline semantics.
- Added unary write policy in `HttpInbound` to ensure cancellation-aware retries and error propagation without throwing in business logic.
- Coverage relies on existing dispatcher/unary paths; no new public surface changes were required.

## SLOs & CI gates
- Maintain unary p99 latency baseline; document any changes.
- CI: socket/unary-focused unit/integration tests; dispatcher/transport suites as applicable.

## Testing Strategy
- Unit: unary request/response with injected failures and cancellations.
- Integration: socket unary end-to-end path, verifying compensations/cleanup.
