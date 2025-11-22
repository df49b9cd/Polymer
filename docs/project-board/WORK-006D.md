# WORK-006D – Error & Observability Semantics

## Goal
Standardize errors, status codes, and observability for control streams.

## Scope
- Error taxonomy with remediation hints.
- Metrics/logs/traces for stream state, lag, rejections, capability mismatches.
- Admin/CLI surfacing of control-stream health.

## Acceptance Criteria
- Errors classified and documented; emitted consistently.
- Dashboards/metrics show stream health; tests assert expected signals on induced faults.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
