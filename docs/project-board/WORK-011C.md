# WORK-011C – Kill Switch for Extensions/Policies

## Goal
Provide remote kill switch to disable an extension or policy quickly.

## Scope
- Control-plane command + propagation via protocol.
- Data-plane behavior: disable/bypass with configured fail-open/closed.
- Audit/logging of activation.

## Acceptance Criteria
- Kill switch tested end-to-end; takes effect within bounded time.
- Audit entry recorded; data-plane respects fail-open/closed choice.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
