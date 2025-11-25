# WORK-024O – http/grpc Duplex (Request/Response + Streaming)

## Goal
Use the Hugo `socket-duplex` tutorial patterns to unify request/response with streaming phases over sockets, preserving compensations and cancellation.

## Scope
- Duplex socket handlers bridging unary setup to streaming body under `src/OmniRelay.DataPlane/Transport/*Socket*` and dispatcher bridging layers.

## Acceptance Criteria
- Duplex sessions built with Hugo channels/wait groups; setup and streaming share a single compensation scope.
- Timeouts/backoff via `ResultPipelineTimers.DelayAsync`/`RetryWithPolicyAsync`; no raw `Task.Delay`/`WhenAll`.
- Tests cover upgrade path from unary to streaming, ensuring resources are released if the upgrade fails mid-handshake.

## Status
Done

## Completion Notes
- Duplex handshake path now disposes the duplex call and WebSocket on handshake/upgrade failures, returning structured Hugo errors instead of leaving resources hanging.
- Existing duplex pumps continue to run under Hugo `ErrGroup` with cancellation backpressure; disposal cleanup is guarded even when the upgrade fails.
- No behavioral change to protocol framing; focus was on compensation/cleanup consistency per tutorial guidance.

## SLOs & CI gates
- No regression in duplex upgrade latency; document changes.
- CI: transport/dispatcher duplex tests; integration where duplex is exercised.

## Testing Strategy
- Unit: upgrade success/failure paths with compensations.
- Integration: duplex end-to-end exercising both unary handshake and streaming body.
