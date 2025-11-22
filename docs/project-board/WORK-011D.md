# WORK-011D – Rollout Audit & History Surfaces

## Goal
Expose audit/history for rollouts in UI/CLI and APIs.

## Scope
- Persist rollout events; provide list/detail endpoints.
- CLI/UX views; filters by artifact/epoch/user.

## Acceptance Criteria
- Audit records show who/what/when/result; CLI outputs tested.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
