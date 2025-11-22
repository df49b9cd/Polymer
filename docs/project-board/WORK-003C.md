# WORK-003C – Native Plugin ABI & Watchdogs

## Goal
Enable loading native plugins via `NativeLibrary.Load` with a stable function-pointer ABI and watchdog enforcement.

## Scope
- Define ABI (function table, version handshake) and sample plugin.
- Signature/manifest verification reused from registry.
- Watchdog/timeouts and failure policy for native calls.

## Acceptance Criteria
- Sample native plugin loads/runs; incompatible ABI rejected with clear error.
- Watchdog triggers terminate/skip per policy; telemetry emitted.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
