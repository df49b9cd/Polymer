# WORK-015B – Simulate/Diff Tools & Validation

## Goal
Provide simulation and diff tools to validate policy bundles before publish.

## Scope
- Simulate/apply diff CLI/API; validate syntax/references/capability fit.
- Golden tests for CLI outputs.

## Acceptance Criteria
- Invalid configs caught pre-publish; CLI returns actionable errors.
- Simulation results reproducible in tests.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
