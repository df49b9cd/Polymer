# WORK-014C – Compatibility Checker & Deprecation Policy

## Goal
Provide tooling to detect configs requiring unavailable capabilities and publish deprecation timelines.

## Scope
- CLI/CI checker that validates configs against capability matrix.
- Document deprecation windows and support policy.

## Acceptance Criteria
- Checker fails builds for incompatible configs; reports missing capabilities.
- Deprecation policy published and referenced in docs.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
