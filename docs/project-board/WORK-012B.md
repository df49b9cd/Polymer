# WORK-012B – Health Rules & SLO Correlation

## Goal
Define health evaluation rules that map telemetry to SLO/regression signals keyed by config epoch and rollout stage.

## Scope
- Rule set for error/latency thresholds; tie to rollout stages.
- Outputs consumed by WORK-011 gates.

## Acceptance Criteria
- Regression in synthetic tests triggers health signals consumed by rollout manager.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
