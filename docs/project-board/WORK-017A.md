# WORK-017A – Core Control/Rollout Commands

## Goal
Implement CLI verbs for config diff/apply/simulate, rollout control, and registry ops.

## Scope
- Commands with table/JSON output; golden tests.
- mTLS/RBAC handling and error messaging.

## Acceptance Criteria
- Commands operate against fixtures; RBAC errors are clear; outputs match goldens.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
