# WORK-003D – Extension Telemetry & Failure Policy Wiring

## Goal
Provide unified telemetry and failure policy handling across DSL, Wasm, and native hosts.

## Scope
- Metrics/logs for load, instantiation, execution latency, watchdog trips, failures.
- Configurable fail-open/closed/reload per extension.
- Admin endpoint summarizing extension state.

## Acceptance Criteria
- Telemetry emitted consistently for all extension types.
- Failure policies enforce correctly in integration/chaos tests.
- Admin endpoint shows per-extension status and last error.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
