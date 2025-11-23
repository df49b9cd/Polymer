# WORK-003D – Extension Telemetry & Failure Policy Wiring

## Goal
Provide unified telemetry and failure policy handling for the DSL host now; extend the same model to Wasm/native when those hosts are reactivated.

## Scope
- Metrics/logs for load, instantiation, execution latency, watchdog trips, failures.
- Configurable fail-open/closed/reload per extension.
- Admin endpoint summarizing extension state.

## Acceptance Criteria
- Telemetry emitted for DSL load/instantiate/execute/watchdog events.
- Failure policies enforced for DSL (fail-open/closed/reload) and covered by tests.
- Admin endpoint shows per-extension status/last error for DSL. Wasm/native will be added when those hosts return to scope.

## Status
Done (DSL focus)

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
