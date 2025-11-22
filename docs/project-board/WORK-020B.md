# WORK-020B – Partition Simulations & LKG Verification

## Goal
Simulate control/registry/CA partitions and verify LKG fallback and recovery behaviors.

## Scope
- Scenarios for control stream drop, CA outage, registry unavailability.
- Assertions on LKG use, admin state, and recovery.

## Acceptance Criteria
- Simulations pass in staging; LKG used correctly; recovery resumes normal operation.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
