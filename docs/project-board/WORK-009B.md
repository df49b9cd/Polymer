# WORK-009B – Watch Lifecycle (Backoff/Resume/State Machine)

## Goal
Provide reusable watch lifecycle with backoff, resume tokens, and health state machine.

## Scope
- Backoff policy implementation; resume token persistence.
- State machine (Connecting/Staging/Active/Degraded) exposed via metrics/admin.

## Acceptance Criteria
- Forced disconnect tests show correct state transitions and resume without missed epochs.

## Status
Done

## Completion Notes
- Watch lifecycle implemented with reconnect/backoff, resume token persistence, and staged apply.
- Health state transitions surfaced through logging and hooks; LKG used when watch unavailable.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
