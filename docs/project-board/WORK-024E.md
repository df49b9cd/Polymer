# WORK-024E – Error Aggregation for Streams

## Goal
Use Hugo stream aggregation helpers (`CollectErrorsAsync`, tap-each variants) where we need full failure visibility instead of first-error short-circuiting.

## Scope
- Shard diff/watch consumers in `src/OmniRelay.ControlPlane/Core/Shards/ControlPlane/ShardControlPlaneService.cs` (diff/watch/stream paths).
- Control-plane agent apply/validate flows in `src/OmniRelay.ControlPlane/Core/Agent/WatchHarness.cs` where multiple updates may carry independent failures.

## Acceptance Criteria
- Streams that must report all failures use `CollectErrorsAsync` (or tap-each aggregate helpers) with aggregated `Error` metadata (counts, keys).
- Default remains short-circuit where appropriate; aggregation is opt-in and documented per method.
- Tests assert multiple-error aggregation and confirm success path remains allocation-lean.

## Status
Done

## Completion Notes
- Control-plane shard watch streams now expose an aggregated path via `CollectWatchAsync`, wrapping repository diff streams with `Result.CollectErrorsAsync` and surfacing `StreamFailure` metadata instead of short-circuiting.
- Agent apply pump aggregates per-lease failures through `Result.CollectErrorsAsync`, logging a single aggregated failure while still completing/poison-handling each lease.

## SLOs & CI gates
- No regression in hot-path allocations; validate with unit perf guards if needed.
- CI: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj`.

## Testing Strategy
- Unit: synthetic streams with mixed successes/failures to verify aggregated error contents.
- Integration: shard diff/watch fixtures to ensure aggregation doesn’t mask cancellations.
- Feature/Hyperscale: not required unless aggregation is enabled in hyperscale scenarios.
