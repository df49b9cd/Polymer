# WORK-009A – Deterministic Startup Pipeline

## Goal
Implement shared startup flow: load/validate LKG, fetch latest snapshot, stage, activate.

## Scope
- Order of operations; failure handling; rollback to safe state.
- Logging of each phase.

## Acceptance Criteria
- Startup follows defined order; failures leave system in non-active safe state.
- Integration tests cover good/bad configs and LKG fallback.

## Status
Done

## Completion Notes
- Startup flow: load LKG from disk, validate, stage, then activate via `WatchHarness`+`MeshAgent`.
- Safe fallback: on failure stays inactive and retains prior snapshot; logs each phase.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
