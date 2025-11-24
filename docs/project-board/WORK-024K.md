# WORK-024K – Dynamic Fan-Out/Fan-In (Selective Routing)

## Goal
Use Hugo dynamic fan-out/in helpers (`SelectFanOutAsync`, `MergeWithStrategyAsync`, selective fan-in patterns from tutorials) for dispatcher and transport routing that depends on per-item decisions.

## Scope
- Dynamic shard/partition routing in `src/OmniRelay.Dispatcher` (routing tables, selective tee/merge).
- Transport-level selective fan-in/fan-out (HTTP/gRPC) in `src/OmniRelay.DataPlane/Transport/Http/*` and `src/OmniRelay.DataPlane/Transport/Grpc/*`.

## Acceptance Criteria
- Dynamic fan-out decisions implemented with Hugo helpers (not manual loops); fan-in respects cancellation, compensations, and error propagation.
- No manual `Task.WhenAny/WhenAll` in these selective routing paths.
- Tests cover dynamic decision changes mid-stream and ensure compensations run for abandoned branches.

## Status
Planned

## SLOs & CI gates
- Maintain or improve routing p99; document any change.
- CI: dispatcher/transport unit + integration suites relevant to routing.

## Testing Strategy
- Unit: dynamic routing tests where destination selection changes; ensure correct fan-in result and cleanup.
- Integration: dispatcher routing feature tests and transport selective fan-in scenarios.
