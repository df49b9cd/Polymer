# WORK-022A – In-Proc vs Sidecar vs Edge Samples

## Goal
Provide runnable samples demonstrating OmniRelay in-proc, sidecar, and edge deployments.

## Scope
- Sample apps/configs for each mode; scripts to run locally/CI.
- Notes on performance/security trade-offs per mode.

## Acceptance Criteria
- Samples run in CI/nightly; outputs documented.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
